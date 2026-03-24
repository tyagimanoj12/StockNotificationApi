using Microsoft.Extensions.Primitives;
using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static StockNotificationApi.Services.MemoryCacheService;

namespace StockNotificationApi.Services
{
    public class StockService : IStockService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StockService> _logger;
        private readonly StockApiSettings _stockApiSettings;
        private readonly IStockListService _stockListService;
        private readonly ICacheService _cache;
        private readonly IRequestCoalescer _requestCoalescer;
        private readonly ICircuitBreakerService _circuitBreakerService;
        private readonly INseApiService _nseApiService;
        private readonly IGoogleFinanceService _googleFinance;
        private readonly IMoneycontrolService _moneycontrol;
        private readonly IAngelOneService _angelOneService;

        // Use constants from Constants.cs
        private const int RATE_LIMIT_DELAY_MS = RateLimitConstants.API_RATE_LIMIT_DELAY_MS;
        private const int DEFAULT_STOCKS_PER_CATEGORY = StockConstants.DEFAULT_STOCKS_PER_CATEGORY;
        private const decimal MARKET_CAP_DIVISOR = StockConstants.MARKET_CAP_DIVISOR;
        private const decimal YEAR_HIGH_MULTIPLIER = StockConstants.YEAR_HIGH_MULTIPLIER;
        private const decimal YEAR_LOW_MULTIPLIER = StockConstants.YEAR_LOW_MULTIPLIER;
        private const decimal DAY_HIGH_MULTIPLIER = StockConstants.DAY_HIGH_MULTIPLIER;
        private const decimal DAY_LOW_MULTIPLIER = StockConstants.DAY_LOW_MULTIPLIER;
        private const int HTTP_TIMEOUT_SECONDS = TimeoutConstants.HTTP_TIMEOUT_SECONDS;

        // Cache constants
        private const int ALL_STOCKS_CACHE_MINUTES = CacheConstants.ALL_STOCKS_CACHE_MINUTES;
        private const int STOCK_DATA_CACHE_MINUTES = CacheConstants.STOCK_DATA_CACHE_MINUTES;

        // Cache key prefixes
        private const string CACHE_PREFIX_ALL_STOCKS = "all_stocks_data";
        private const string CACHE_PREFIX_STOCK = "stock";
        private const string CACHE_PREFIX_STOCK_PRICE = "stock_price";
        private const string CACHE_PREFIX_FAILED_SYMBOL = "failed_symbol";

        // Semaphore for concurrent operations
        private static readonly SemaphoreSlim _fetchAllStocksLock = new(1, 1);
        private static DateTime _lastFetchAttempt = DateTime.MinValue;
        private static readonly TimeSpan MinimumFetchInterval = TimeSpan.FromSeconds(10);

        // Cache for failed symbols to avoid repeated attempts
        private static readonly ConcurrentDictionary<string, DateTime> _failedSymbols = new();
        private static readonly TimeSpan FailedSymbolCacheTime = TimeSpan.FromMinutes(15);

        // Track which APIs are currently failing
        private static readonly ConcurrentDictionary<string, bool> _failingApis = new();

        // Endpoints
        private const string NSE_QUOTE_URL = EndpointConstants.NSE_QUOTE_URL;
        private const string BSE_QUOTE_URL = EndpointConstants.BSE_QUOTE_URL;
        private const string YAHOO_QUOTE_URL = EndpointConstants.YAHOO_QUOTE_URL;

        public StockService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IStockListService stockListService,
            ICacheService cache,
            ILogger<StockService> logger,
            IRequestCoalescer requestCoalescer,
            ICircuitBreakerService circuitBreakerService,
            INseApiService nseApiService,
            IGoogleFinanceService googleFinance,
            IMoneycontrolService moneycontrol,
            IAngelOneService angelOneService)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _stockListService = stockListService ?? throw new ArgumentNullException(nameof(stockListService));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _requestCoalescer = requestCoalescer ?? throw new ArgumentNullException(nameof(requestCoalescer));
            _circuitBreakerService = circuitBreakerService ?? throw new ArgumentNullException(nameof(circuitBreakerService));
            _nseApiService = nseApiService ?? throw new ArgumentNullException(nameof(nseApiService));
            _googleFinance = googleFinance ?? throw new ArgumentNullException(nameof(googleFinance));
            _moneycontrol = moneycontrol ?? throw new ArgumentNullException(nameof(moneycontrol));
            _angelOneService = angelOneService ?? throw new ArgumentNullException(nameof(angelOneService));

            _stockApiSettings = configuration.GetSection("StockApiSettings").Get<StockApiSettings>()
                ?? throw new ArgumentNullException(nameof(configuration), "StockApiSettings not configured");
        }

        public async Task<List<StockData>> GetIndianStockDataAsync()
        {
            return await _requestCoalescer.GetOrAddAsync(CACHE_PREFIX_ALL_STOCKS, async () =>
            {
                return await GetCachedAllStocksAsync();
            });
        }

        private async Task<List<StockData>> GetCachedAllStocksAsync()
        {
            return await _cache.GetOrSetAsync(CACHE_PREFIX_ALL_STOCKS, async () =>
            {
                _logger.LogInformation("Cache miss for all stocks, fetching...");
                return await FetchAllStocksDataAsync();
            }, TimeSpan.FromMinutes(ALL_STOCKS_CACHE_MINUTES)) ?? new List<StockData>();
        }

        private async Task<List<StockData>> FetchAllStocksDataAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching stocks for market analysis...");

            await _fetchAllStocksLock.WaitAsync(cancellationToken);
            try
            {
                if (DateTime.UtcNow - _lastFetchAttempt < MinimumFetchInterval)
                {
                    var cached = await _cache.GetAsync<List<StockData>>(CACHE_PREFIX_ALL_STOCKS);
                    if (cached != null && cached.Any())
                    {
                        _logger.LogDebug("Returning cached stocks due to rate limiting");
                        return cached;
                    }
                }

                _lastFetchAttempt = DateTime.UtcNow;

                var categories = await _stockListService.GetAllCategoriesAsync();

                // Filter out ETF/MF symbols before fetching
                var stocksToFetch = new List<StockInfo>();
                foreach (var stock in categories["LargeCap"].Take(DEFAULT_STOCKS_PER_CATEGORY))
                {
                    if (!IsETFOrMutualFund(stock.Symbol))
                        stocksToFetch.Add(stock);
                }
                foreach (var stock in categories["MidCap"].Take(DEFAULT_STOCKS_PER_CATEGORY))
                {
                    if (!IsETFOrMutualFund(stock.Symbol))
                        stocksToFetch.Add(stock);
                }
                foreach (var stock in categories["SmallCap"].Take(DEFAULT_STOCKS_PER_CATEGORY))
                {
                    if (!IsETFOrMutualFund(stock.Symbol))
                        stocksToFetch.Add(stock);
                }

                _logger.LogInformation("Fetching data for {Count} stocks (filtered from {Total} total)",
                    stocksToFetch.Count, DEFAULT_STOCKS_PER_CATEGORY * 3);

                var symbols = stocksToFetch.Select(s => $"{s.Symbol}.NS").ToList();

                var stockDataMap = new Dictionary<string, StockData>();
                var failedSymbols = new List<string>();

                // Try Angel One bulk fetch
                if (await _circuitBreakerService.IsApiAvailableAsync("AngelOne"))
                {
                    _logger.LogInformation("Attempting bulk fetch of {Count} stocks from Angel One", symbols.Count);

                    try
                    {
                        var bulkStocks = await _circuitBreakerService.ExecuteAsync(
                            "AngelOne",
                            async () => await _angelOneService.GetMultipleQuotesAsync(symbols),
                            new List<StockData>()
                        );

                        if (bulkStocks != null && bulkStocks.Any())
                        {
                            _logger.LogInformation("✅ Angel One bulk fetch successful for {Count} stocks", bulkStocks.Count);

                            foreach (var stock in bulkStocks)
                            {
                                if (stock != null && !string.IsNullOrEmpty(stock.Symbol))
                                {
                                    stockDataMap[stock.Symbol] = stock;
                                }
                            }

                            foreach (var symbol in symbols)
                            {
                                var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "");
                                if (!stockDataMap.ContainsKey(symbol) && !stockDataMap.ContainsKey(cleanSymbol))
                                {
                                    failedSymbols.Add(symbol);
                                }
                            }

                            _logger.LogInformation("Bulk fetch successful for {SuccessCount}/{TotalCount} stocks, {FailedCount} failed",
                                stockDataMap.Count, symbols.Count, failedSymbols.Count);
                        }
                        else
                        {
                            _logger.LogWarning("Angel One bulk fetch returned no data");
                            failedSymbols.AddRange(symbols);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Angel One bulk fetch failed");
                        failedSymbols.AddRange(symbols);
                    }
                }
                else
                {
                    _logger.LogWarning("Angel One circuit breaker is open");
                    failedSymbols.AddRange(symbols);
                }

                // Process failed symbols with better concurrency control
                if (failedSymbols.Any())
                {
                    _logger.LogInformation("Fetching {Count} failed stocks individually with fallback", failedSymbols.Count);

                    // Filter out recently failed symbols
                    var symbolsToRetry = new List<string>();
                    foreach (var symbol in failedSymbols)
                    {
                        var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "");
                        if (!IsRecentlyFailed(cleanSymbol))
                        {
                            symbolsToRetry.Add(symbol);
                        }
                        else
                        {
                            _logger.LogDebug("Skipping recently failed symbol: {Symbol}", symbol);
                        }
                    }

                    if (symbolsToRetry.Any())
                    {
                        using var semaphore = new SemaphoreSlim(3);
                        var tasks = new List<Task>();
                        var lockObj = new object();

                        foreach (var symbol in symbolsToRetry)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;

                            tasks.Add(Task.Run(async () =>
                            {
                                await semaphore.WaitAsync(cancellationToken);
                                try
                                {
                                    var stockData = await FetchSingleStockWithFallbackAsync(symbol);
                                    if (stockData != null)
                                    {
                                        lock (lockObj)
                                        {
                                            var cleanSymbol = stockData.Symbol;
                                            if (!stockDataMap.ContainsKey(cleanSymbol))
                                            {
                                                stockDataMap[cleanSymbol] = stockData;
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Error fetching data for {Symbol}", symbol);
                                    MarkAsFailed(symbol);
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }, cancellationToken));
                        }

                        await Task.WhenAll(tasks);
                    }
                }

                var allStocks = stockDataMap.Values.ToList();
                _logger.LogInformation("Successfully fetched {Count} stocks", allStocks.Count);
                return allStocks;
            }
            finally
            {
                _fetchAllStocksLock.Release();
            }
        }

        private bool IsRecentlyFailed(string symbol)
        {
            if (_failedSymbols.TryGetValue(symbol, out var failedTime))
            {
                if (DateTime.UtcNow - failedTime < FailedSymbolCacheTime)
                {
                    return true;
                }
                _failedSymbols.TryRemove(symbol, out _);
            }
            return false;
        }

        private void MarkAsFailed(string symbol)
        {
            var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Trim();
            _failedSymbols[cleanSymbol] = DateTime.UtcNow;
        }

        private async Task<StockData?> FetchSingleStockWithFallbackAsync(string symbol)
        {
            var cleanSymbol = symbol.Replace(".BSE", "").Replace(".NSE", "").Replace(".NS", "").Replace(".BO", "").Trim();

            // Skip ETF/MF symbols
            if (IsETFOrMutualFund(cleanSymbol))
            {
                _logger.LogDebug("Skipping ETF/Mutual Fund {Symbol}", cleanSymbol);
                return null;
            }

            // Check if recently failed
            if (IsRecentlyFailed(cleanSymbol))
            {
                _logger.LogDebug("Skipping recently failed symbol: {Symbol}", cleanSymbol);
                return null;
            }

            _logger.LogDebug("Fetching {Symbol} with fallback", cleanSymbol);
            StockData? stockData = null;

            // TRY 1: Angel One (individual)
            if (await _circuitBreakerService.IsApiAvailableAsync("AngelOne"))
            {
                try
                {
                    stockData = await _circuitBreakerService.ExecuteAsync(
                        "AngelOne",
                        async () => await _angelOneService.GetLiveQuoteAsync(cleanSymbol),
                        null
                    );
                    if (stockData != null && stockData.Price > 0)
                    {
                        _logger.LogDebug("✅ Angel One successful for {Symbol}", cleanSymbol);
                        return stockData;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Angel One failed for {Symbol}", cleanSymbol);
                }
            }

            // TRY 2: Yahoo Finance (Most reliable fallback)
            if (stockData == null && await _circuitBreakerService.IsApiAvailableAsync("Yahoo"))
            {
                try
                {
                    stockData = await _circuitBreakerService.ExecuteAsync(
                        "Yahoo",
                        async () => await GetFromYahooFinanceAsync(cleanSymbol),
                        null
                    );
                    if (stockData != null && stockData.Price > 0)
                    {
                        _logger.LogDebug("✅ Yahoo Finance successful for {Symbol}", cleanSymbol);
                        return stockData;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Yahoo Finance failed for {Symbol}", cleanSymbol);
                }
            }

            // TRY 3: Google Finance (If Yahoo fails)
            if (stockData == null && await _circuitBreakerService.IsApiAvailableAsync("GoogleFinance"))
            {
                try
                {
                    stockData = await _circuitBreakerService.ExecuteAsync(
                        "GoogleFinance",
                        async () => await _googleFinance.GetQuoteAsync(cleanSymbol),
                        null
                    );
                    if (stockData != null && stockData.Price > 0)
                    {
                        _logger.LogDebug("✅ Google Finance successful for {Symbol}", cleanSymbol);
                        return stockData;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Google Finance failed for {Symbol}", cleanSymbol);
                }
            }

            // If all failed, mark as failed to avoid repeated attempts
            if (stockData == null)
            {
                MarkAsFailed(cleanSymbol);
            }

            return stockData;
        }

        public async Task<StockData?> GetStockDataAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Empty symbol provided");
                return null;
            }

            var cacheKey = GetCacheKey(CACHE_PREFIX_STOCK, symbol);

            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                return await FetchStockDataAsync(symbol);
            }, TimeSpan.FromMinutes(STOCK_DATA_CACHE_MINUTES));
        }

        private string GetCacheKey(string prefix, params object[] parameters)
        {
            return $"{prefix}_{string.Join("_", parameters)}";
        }

        private async Task<StockData?> FetchStockDataAsync(string symbol)
        {
            var cleanSymbol = symbol.Replace(".BSE", "").Replace(".NSE", "").Replace(".NS", "").Replace(".BO", "").Trim();

            // Skip ETF/MF symbols early
            if (IsETFOrMutualFund(cleanSymbol))
            {
                _logger.LogDebug("Skipping ETF/Mutual Fund {Symbol}", cleanSymbol);
                return null;
            }

            _logger.LogDebug("Fetching data for {Symbol}", cleanSymbol);
            StockData? stockData = null;

            // PRIMARY: Angel One
            if (await _circuitBreakerService.IsApiAvailableAsync("AngelOne"))
            {
                try
                {
                    stockData = await _circuitBreakerService.ExecuteAsync(
                        "AngelOne",
                        async () => await _angelOneService.GetLiveQuoteAsync(cleanSymbol),
                        null
                    );
                    if (stockData != null && stockData.Price > 0)
                    {
                        _logger.LogDebug("✅ Angel One successful for {Symbol}", cleanSymbol);
                        return stockData;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Angel One failed for {Symbol}", cleanSymbol);
                }
            }

            // SECONDARY: Yahoo Finance
            if (stockData == null && await _circuitBreakerService.IsApiAvailableAsync("Yahoo"))
            {
                try
                {
                    stockData = await _circuitBreakerService.ExecuteAsync(
                        "Yahoo",
                        async () => await GetFromYahooFinanceAsync(cleanSymbol),
                        null
                    );
                    if (stockData != null && stockData.Price > 0)
                    {
                        _logger.LogDebug("✅ Yahoo Finance successful for {Symbol}", cleanSymbol);
                        return stockData;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Yahoo Finance failed for {Symbol}", cleanSymbol);
                }
            }

            // If all failed, return cached data if available
            var cached = await _cache.GetAsync<StockData>(GetCacheKey(CACHE_PREFIX_STOCK, symbol));
            if (cached != null)
            {
                _logger.LogDebug("Returning cached data for {Symbol}", symbol);
                return cached;
            }

            return null;
        }

        public async Task<List<StockData>> GetMultipleQuotesAsync(List<string> symbols)
        {
            if (symbols == null || !symbols.Any())
                return new List<StockData>();

            // Filter out ETF/MF symbols first
            var filteredSymbols = symbols
                .Where(s => !IsETFOrMutualFund(s.Replace(".NS", "").Replace(".NSE", "")))
                .ToList();

            _logger.LogInformation("Fetching quotes for {Count} symbols (filtered from {Total})",
                filteredSymbols.Count, symbols.Count);

            if (!filteredSymbols.Any())
                return new List<StockData>();

            // Use Angel One's bulk quote method
            if (await _circuitBreakerService.IsApiAvailableAsync("AngelOne"))
            {
                try
                {
                    return await _circuitBreakerService.ExecuteAsync(
                        "AngelOne",
                        async () => await _angelOneService.GetMultipleQuotesAsync(filteredSymbols),
                        new List<StockData>());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Angel One bulk fetch failed");
                }
            }

            // Fallback to individual fetches with concurrency limit
            _logger.LogInformation("Falling back to individual fetches for {Count} symbols", filteredSymbols.Count);

            var results = new List<StockData>();
            using var semaphore = new SemaphoreSlim(3);
            var tasks = filteredSymbols.Select(async symbol =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var data = await GetStockDataAsync(symbol);
                    if (data != null)
                    {
                        lock (results)
                        {
                            results.Add(data);
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results;
        }

        private bool IsETFOrMutualFund(string symbol)
        {
            var etfPatterns = new[]
            {
                "INAV", "ETF", "BEES", "MF", "MUTUAL", "SM", "SG",
                "GB", "ND", "NG", "NH", "Z6", "Z7", "BANKETF", "BANKIETF",
                "JUNIORBEES", "LIQUIDBEES", "MON100", "NIFTYBEES", "NIFTYBETA",
                "HDFCNIF100", "HDFCNIFIT", "ICICI5INAV", "MONQ50INAV", "DSPN50INAV",
                "AXISILVER", "SILVERADD", "EBBETF0433", "GSEC10IETF", "QNIFTY",
                "LOWVOL", "HDFCGROWTH", "MAM150INAV", "HSM250INAV"
            };

            var suffixPatterns = new[]
            {
                "-EQ", "-SG", "-GB", "-MF", "-SM", "-BE", "-IV", "-ND",
                "-NG", "-NH", "-Z6", "-Z7", "-ST"
            };

            var upperSymbol = symbol.ToUpper();

            if (etfPatterns.Any(p => upperSymbol.Contains(p)))
                return true;

            if (suffixPatterns.Any(p => upperSymbol.EndsWith(p)))
                return true;

            if (upperSymbol.Length > 5 && upperSymbol.Any(char.IsDigit) && !IsValidStockSymbol(upperSymbol))
                return true;

            return false;
        }

        private bool IsValidStockSymbol(string symbol)
        {
            var validStockPatterns = new[]
            {
                "63MOONS", "63", "MOONS", "20MICRONS", "3MINDIA", "5PAISA"
            };

            return validStockPatterns.Any(p => symbol.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        #region Yahoo Finance Implementation

        private async Task<StockData?> GetFromYahooFinanceAsync(string symbol)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                client.Timeout = TimeSpan.FromSeconds(10);

                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}.NS?region=IN&lang=en-IN";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    if (string.IsNullOrWhiteSpace(content) || !content.TrimStart().StartsWith("{"))
                        return null;

                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;

                    if (root.TryGetProperty("chart", out var chart) &&
                        chart.TryGetProperty("result", out var result) &&
                        result.GetArrayLength() > 0)
                    {
                        var firstResult = result[0];
                        if (firstResult.TryGetProperty("meta", out var meta))
                        {
                            return MapYahooResponse(meta, symbol);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Yahoo Finance failed for {Symbol}", symbol);
            }
            return null;
        }

        private StockData MapYahooResponse(JsonElement meta, string symbol)
        {
            var price = GetDecimalProperty(meta, "regularMarketPrice") ?? 0;
            var prevClose = GetDecimalProperty(meta, "previousClose") ?? price;
            var change = price - prevClose;
            var changePercent = prevClose > 0 ? (change / prevClose) * 100 : 0;

            return new StockData
            {
                Symbol = symbol,
                Name = GetStringProperty(meta, "symbol") ?? symbol,
                Exchange = "NSE",
                Price = price,
                Change = change,
                ChangePercent = changePercent,
                DayHigh = GetDecimalProperty(meta, "regularMarketDayHigh") ?? price * DAY_HIGH_MULTIPLIER,
                DayLow = GetDecimalProperty(meta, "regularMarketDayLow") ?? price * DAY_LOW_MULTIPLIER,
                Open = GetDecimalProperty(meta, "regularMarketOpen") ?? price,
                PreviousClose = prevClose,
                Volume = GetLongProperty(meta, "regularMarketVolume") ?? 0,
                YearHigh = price * YEAR_HIGH_MULTIPLIER,
                YearLow = price * YEAR_LOW_MULTIPLIER,
                Timestamp = DateTime.Now
            };
        }

        #endregion

        #region Helper Methods

        private decimal? GetDecimalProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
                return prop.GetDecimal();
            return null;
        }

        private long? GetLongProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
                return prop.GetInt64();
            return null;
        }

        private string? GetStringProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            return null;
        }

        #endregion

        #region Public Utility Methods

        public void ClearCache()
        {
            _cache.RemoveByPattern(CACHE_PREFIX_ALL_STOCKS);
            _cache.RemoveByPattern(CACHE_PREFIX_STOCK);
            _failedSymbols.Clear();
            _logger.LogInformation("Cleared all stock cache");
        }

        public async Task<CacheStatistics> GetCacheStatisticsAsync()
        {
            return await Task.FromResult(_cache.GetStatistics());
        }

        #endregion

        #region Stub Methods (Keep for interface compliance, but not actively used)

        private bool IsMarketHours() => false;

        private async Task<StockData?> GetFromNSEAsync(string symbol) => null;

        private async Task<StockData?> GetFromBSEAsync(string symbol) => null;

        private string GetSector(string symbol) => "Other";

        private string GetIndustry(string symbol) => "General";

        private string GetCompanyName(string symbol) => symbol;

        #endregion
    }
}