using Microsoft.Extensions.DependencyInjection;
using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Collections.Concurrent;

namespace StockNotificationApi.Services
{
    public class StockListService : IStockListService
    {
        private readonly ILogger<StockListService> _logger;
        private readonly ICacheService _cache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAngelOneService _angelOneService;

        // Refresh lock to prevent multiple simultaneous refreshes
        private static readonly SemaphoreSlim _refreshLock = new(1, 1);
        private static readonly SemaphoreSlim _populationLock = new(1, 1);
        private static bool _isRefreshing = false;
        private static DateTime _lastRefreshAttempt = DateTime.MinValue;
        private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(5);

        // Use constants from Constants.cs
        private const int LARGE_CAP_THRESHOLD = MarketConstants.LARGE_CAP_THRESHOLD;
        private const int MID_CAP_THRESHOLD = MarketConstants.MID_CAP_THRESHOLD;
        private const int DEFAULT_STOCK_COUNT = StockConstants.DEFAULT_STOCK_COUNT;
        private const int CACHE_DURATION_HOURS = CacheConstants.STOCK_LIST_CACHE_HOURS;
        private const int FNO_LIST_SIZE = StockConstants.FNO_LIST_SIZE;

        // Cache for stock lists
        private static List<StockInfo> _cachedStocks = new();
        private static List<string> _cachedFNOSymbols = new();
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(CACHE_DURATION_HOURS);

        // Cache for market cap enriched stocks
        private static ConcurrentDictionary<string, List<StockInfo>> _enrichedStocksCache = new();

        public StockListService(
            IHttpClientFactory httpClientFactory,
            ILogger<StockListService> logger,
            ICacheService cache,
            IServiceScopeFactory scopeFactory,
            IAngelOneService angelOneService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _angelOneService = angelOneService ?? throw new ArgumentNullException(nameof(angelOneService));
        }

        public async Task<List<StockInfo>> GetNSEStocksAsync()
        {
            // Return cached list if not expired
            if (_cachedStocks.Any() && DateTime.UtcNow - _lastRefresh < RefreshInterval)
            {
                _logger.LogDebug("Returning {Count} cached NSE stocks", _cachedStocks.Count);
                return new List<StockInfo>(_cachedStocks);
            }

            await RefreshStockListAsync();
            return new List<StockInfo>(_cachedStocks);
        }

        public async Task<List<StockInfo>> GetBSEStocksAsync()
        {
            await RefreshStockListAsync();
            return new List<StockInfo>(_cachedStocks.Where(s => s.Exchange == "BSE"));
        }

        public async Task<List<StockInfo>> GetTopStocksByMarketCapAsync(int count = 50)
        {
            if (count <= 0)
            {
                _logger.LogWarning("Invalid count requested: {Count}", count);
                return new List<StockInfo>();
            }

            var stocksWithCap = await GetStocksWithMarketCapAsync(Math.Max(count, DEFAULT_STOCK_COUNT));
            return stocksWithCap.Take(count).ToList();
        }

        public async Task<List<StockInfo>> GetLargeCapStocksAsync(int count = 20)
        {
            if (count <= 0) return new List<StockInfo>();

            var stocks = await GetStocksWithMarketCapAsync(DEFAULT_STOCK_COUNT);
            return stocks.Where(s => s.MarketCap >= LARGE_CAP_THRESHOLD)
                        .Take(count)
                        .ToList();
        }

        public async Task<List<StockInfo>> GetMidCapStocksAsync(int count = 20)
        {
            if (count <= 0) return new List<StockInfo>();

            var stocks = await GetStocksWithMarketCapAsync(DEFAULT_STOCK_COUNT);
            return stocks.Where(s => s.MarketCap >= MID_CAP_THRESHOLD && s.MarketCap < LARGE_CAP_THRESHOLD)
                        .Take(count)
                        .ToList();
        }

        public async Task<List<StockInfo>> GetSmallCapStocksAsync(int count = 20)
        {
            if (count <= 0) return new List<StockInfo>();

            var stocks = await GetStocksWithMarketCapAsync(DEFAULT_STOCK_COUNT);
            return stocks.Where(s => s.MarketCap < MID_CAP_THRESHOLD)
                        .Take(count)
                        .ToList();
        }

        public async Task<Dictionary<string, List<StockInfo>>> GetAllCategoriesAsync()
        {
            var stocks = await GetStocksWithMarketCapAsync(DEFAULT_STOCK_COUNT);

            return new Dictionary<string, List<StockInfo>>
            {
                ["LargeCap"] = stocks.Where(s => s.MarketCap >= LARGE_CAP_THRESHOLD).ToList(),
                ["MidCap"] = stocks.Where(s => s.MarketCap >= MID_CAP_THRESHOLD && s.MarketCap < LARGE_CAP_THRESHOLD).ToList(),
                ["SmallCap"] = stocks.Where(s => s.MarketCap < MID_CAP_THRESHOLD).ToList()
            };
        }

        public async Task<List<StockInfo>> GetStocksWithMarketCapAsync(int count = 100)
        {
            var cacheKey = $"StocksWithMarketCap_{count}";

            // Check if cache exists
            var cached = _cache.Get<List<StockInfo>>(cacheKey);
            if (cached != null && cached.Any())
                return cached;

            // Check in-memory cache
            if (_enrichedStocksCache.TryGetValue(cacheKey, out var memoryCached) && memoryCached.Any())
                return memoryCached;

            // Get fresh stocks from Angel One (with lock to prevent multiple parallel populations)
            await _populationLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                cached = _cache.Get<List<StockInfo>>(cacheKey);
                if (cached != null && cached.Any())
                    return cached;

                if (_enrichedStocksCache.TryGetValue(cacheKey, out memoryCached) && memoryCached.Any())
                    return memoryCached;

                var stocks = await PopulateStocksWithMarketCapAsync(count);

                // Cache the result
                _cache.Set(cacheKey, stocks, TimeSpan.FromHours(CACHE_DURATION_HOURS));
                _enrichedStocksCache[cacheKey] = stocks;

                return stocks;
            }
            finally
            {
                _populationLock.Release();
            }
        }

        private async Task<List<StockInfo>> PopulateStocksWithMarketCapAsync(int count)
        {
            _logger.LogInformation("Populating {Count} stocks with market cap data from Angel One...", count);

            // Get master quote from Angel One (NOW USING SCRIP MASTER)
            var masterQuotes = await _angelOneService.GetMasterQuoteAsync("NSE");

            if (masterQuotes == null || !masterQuotes.Any())
            {
                _logger.LogWarning("No stocks available from Angel One scrip master");
                return GetFallbackStocks(count);
            }

            // Filter for equity stocks only
            var equityStocks = masterQuotes.Where(q => string.IsNullOrEmpty(q.InstrumentType) || q.InstrumentType == "EQ").ToList();
            if (!equityStocks.Any())
            {
                _logger.LogWarning("No equity stocks found in scrip master");
                return GetFallbackStocks(count);
            }

            _logger.LogInformation("Found {Total} total stocks, {Equity} equity stocks in scrip master",
                masterQuotes.Count, equityStocks.Count);

            // Take top N by market cap (we'll sort after getting market data)
            var topStocks = equityStocks.Take(Math.Min(count, equityStocks.Count)).ToList();
            var symbols = topStocks.Select(q => q.Symbol).ToList();

            _logger.LogInformation("Fetching market data for {Count} stocks in a single bulk call", symbols.Count);

            // Use bulk API call instead of individual calls
            List<StockData> stockDataList = new List<StockData>();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                // SINGLE bulk call to get all stock data
                stockDataList = await stockService.GetMultipleQuotesAsync(symbols);

                _logger.LogInformation("✅ Bulk fetch returned {Count} stock data entries", stockDataList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching bulk stock data, falling back to individual calls");
                // Fallback to individual calls if bulk fails
                stockDataList = await FetchIndividualStockDataAsync(symbols);
            }

            // Create a dictionary for quick lookup
            var stockDataDict = stockDataList
                .Where(s => s != null)
                .ToDictionary(s => s.Symbol, s => s, StringComparer.OrdinalIgnoreCase);

            var enrichedStocks = new List<StockInfo>();

            foreach (var quote in topStocks)
            {
                var stockData = stockDataDict.TryGetValue(quote.Symbol, out var data) ? data : null;

                var enrichedStock = new StockInfo
                {
                    Symbol = quote.Symbol,
                    CompanyName = quote.CompanyName ?? quote.TradingSymbol ?? stockData?.Name ?? quote.Symbol,
                    Exchange = quote.Exchange ?? "NSE",
                    Sector = quote.Sector ?? stockData?.Sector ?? GetSector(quote.Symbol),
                    Industry = quote.Industry ?? stockData?.Industry ?? GetIndustry(quote.Symbol),
                    MarketCap = stockData?.MarketCap ?? CalculateMarketCapFromPrice(stockData?.Price ?? 0, 0),
                    IsFNOSec = quote.IsFNOSec || IsLikelyFNOSymbol(quote.Symbol)
                };

                enrichedStocks.Add(enrichedStock);
            }

            // Sort by market cap (if available) or alphabetically
            var sortedStocks = enrichedStocks
                .OrderByDescending(s => s.MarketCap)
                .ThenBy(s => s.Symbol)
                .ToList();

            _logger.LogInformation("Successfully populated {Count} stocks with market cap data", sortedStocks.Count);

            return sortedStocks;
        }

        private decimal CalculateMarketCapFromPrice(decimal price, decimal outstandingShares = 0)
        {
            // Simplified market cap calculation (rough estimate)
            // In production, you'd want actual data from the stock data service
            return price * 1000000; // Placeholder
        }

        // Fallback method if bulk call fails
        private async Task<List<StockData>> FetchIndividualStockDataAsync(List<string> symbols)
        {
            _logger.LogWarning("Falling back to individual stock data fetching for {Count} symbols", symbols.Count);

            var results = new ConcurrentBag<StockData>();
            using var semaphore = new SemaphoreSlim(5); // Limit concurrent calls
            var tasks = new List<Task>();

            using var scope = _scopeFactory.CreateScope();
            var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

            foreach (var symbol in symbols)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var stockData = await stockService.GetStockDataAsync(symbol);
                        if (stockData != null)
                        {
                            results.Add(stockData);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to fetch data for {Symbol}", symbol);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            return results.ToList();
        }

        private List<StockInfo> GetFallbackStocks(int count)
        {
            _logger.LogWarning("Returning fallback stocks");

            var fallbackSymbols = new List<string>
            {
                "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
                "HINDUNILVR", "ITC", "SBIN", "BHARTIARTL", "KOTAKBANK",
                "LT", "ASIANPAINT", "MARUTI", "TATAMOTORS", "AXISBANK",
                "HCLTECH", "SUNPHARMA", "TITAN", "WIPRO", "ULTRACEMCO",
                "BAJFINANCE", "ADANIPORTS", "NTPC", "POWERGRID", "ONGC"
            }.Take(count).ToList();

            var stocks = new List<StockInfo>();

            foreach (var symbol in fallbackSymbols)
            {
                stocks.Add(new StockInfo
                {
                    Symbol = symbol,
                    CompanyName = GetCompanyName(symbol),
                    Exchange = "NSE",
                    Sector = GetSector(symbol),
                    Industry = GetIndustry(symbol),
                    MarketCap = 0,
                    IsFNOSec = IsLikelyFNOSymbol(symbol)
                });
            }

            return stocks;
        }

        public async Task RefreshStockListAsync()
        {
            // Prevent multiple simultaneous refreshes
            await _refreshLock.WaitAsync();
            try
            {
                // Check if a refresh was recently attempted
                if (_isRefreshing)
                {
                    _logger.LogDebug("Refresh already in progress, skipping...");
                    return;
                }

                // Rate limit refresh attempts
                if (DateTime.UtcNow - _lastRefreshAttempt < MinimumRefreshInterval && _cachedStocks.Any())
                {
                    _logger.LogDebug("Refresh attempted too soon, using cached data");
                    return;
                }

                _isRefreshing = true;
                _lastRefreshAttempt = DateTime.UtcNow;

                _logger.LogInformation("Refreshing stock list from Angel One scrip master...");

                // Get master quote from Angel One (NOW USING SCRIP MASTER)
                var masterQuotes = await _angelOneService.GetMasterQuoteAsync("NSE");

                if (masterQuotes != null && masterQuotes.Any())
                {
                    var newStockList = new List<StockInfo>();
                    var fnoSymbols = new List<string>();

                    // Filter for equity stocks (EQ) and indices if needed
                    var equityQuotes = masterQuotes.Where(q => string.IsNullOrEmpty(q.InstrumentType) || q.InstrumentType == "EQ").ToList();
                    _logger.LogInformation("Found {Total} total stocks, {Equity} equity stocks in scrip master",
                        masterQuotes.Count, equityQuotes.Count);

                    foreach (var quote in equityQuotes)
                    {
                        newStockList.Add(new StockInfo
                        {
                            Symbol = quote.Symbol,
                            CompanyName = quote.CompanyName ?? quote.TradingSymbol ?? quote.Symbol,
                            Exchange = quote.Exchange ?? "NSE",
                            Sector = quote.Sector,
                            Industry = quote.Industry,
                            IsFNOSec = quote.IsFNOSec || IsLikelyFNOSymbol(quote.Symbol)
                        });

                        if (quote.IsFNOSec || IsLikelyFNOSymbol(quote.Symbol))
                        {
                            fnoSymbols.Add(quote.Symbol);
                        }
                    }

                    _cachedStocks = newStockList;
                    _cachedFNOSymbols = fnoSymbols;
                    _lastRefresh = DateTime.UtcNow;

                    _logger.LogInformation("✅ Successfully loaded {Count} equity stocks from Angel One scrip master",
                        _cachedStocks.Count);
                }
                else
                {
                    _logger.LogWarning("No symbols found in Angel One scrip master response");
                    LoadFallbackStocks();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing stock list from Angel One");
                LoadFallbackStocks();
            }
            finally
            {
                _isRefreshing = false;
                _refreshLock.Release();
            }
        }

        public async Task<List<StockInfo>> GetStocksBySectorAsync(string sector)
        {
            if (string.IsNullOrWhiteSpace(sector))
            {
                _logger.LogWarning("Empty sector provided");
                return new List<StockInfo>();
            }

            var allStocks = await GetNSEStocksAsync();
            return allStocks.Where(s => s.Sector?.Contains(sector, StringComparison.OrdinalIgnoreCase) == true)
                           .ToList();
        }

        public async Task<List<string>> GetFNOSymbolsAsync(int count = 50)
        {
            if (count <= 0) return new List<string>();

            await RefreshStockListAsync();
            return _cachedStocks.Where(s => s.IsFNOSec)
                                .Take(count)
                                .Select(s => s.Symbol)
                                .ToList();
        }

        // Method to clear enriched stocks cache
        public void ClearEnrichedStocksCache()
        {
            _enrichedStocksCache.Clear();
            _logger.LogInformation("Cleared enriched stocks cache");
        }

        #region Private Helper Methods

        private bool IsLikelyFNOSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

            // F&O stocks list - you can expand this or get from NSE API
            var fnoCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
                "HINDUNILVR", "ITC", "SBIN", "BHARTIARTL", "KOTAKBANK",
                "LT", "ASIANPAINT", "MARUTI", "TATAMOTORS", "AXISBANK",
                "HCLTECH", "SUNPHARMA", "TITAN", "WIPRO", "ULTRACEMCO",
                "BAJFINANCE", "ADANIPORTS", "NTPC", "POWERGRID", "ONGC"
            };

            return fnoCandidates.Contains(symbol);
        }

        private string GetCompanyName(string symbol)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = "Reliance Industries Ltd",
                ["TCS"] = "Tata Consultancy Services Ltd",
                ["HDFCBANK"] = "HDFC Bank Ltd",
                ["INFY"] = "Infosys Ltd",
                ["ICICIBANK"] = "ICICI Bank Ltd",
                ["HINDUNILVR"] = "Hindustan Unilever Ltd",
                ["ITC"] = "ITC Ltd",
                ["SBIN"] = "State Bank of India",
                ["BHARTIARTL"] = "Bharti Airtel Ltd",
                ["KOTAKBANK"] = "Kotak Mahindra Bank Ltd",
                ["LT"] = "Larsen & Toubro Ltd",
                ["ASIANPAINT"] = "Asian Paints Ltd",
                ["MARUTI"] = "Maruti Suzuki India Ltd",
                ["TATAMOTORS"] = "Tata Motors Ltd",
                ["AXISBANK"] = "Axis Bank Ltd",
                ["HCLTECH"] = "HCL Technologies Ltd",
                ["SUNPHARMA"] = "Sun Pharmaceutical Industries Ltd",
                ["TITAN"] = "Titan Company Ltd",
                ["WIPRO"] = "Wipro Ltd",
                ["ULTRACEMCO"] = "UltraTech Cement Ltd"
            };

            return names.TryGetValue(symbol, out var name) ? name : symbol;
        }

        private string GetSector(string symbol)
        {
            var sectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = "Energy",
                ["TCS"] = "Technology",
                ["HDFCBANK"] = "Banking",
                ["INFY"] = "Technology",
                ["ICICIBANK"] = "Banking",
                ["HINDUNILVR"] = "FMCG",
                ["ITC"] = "FMCG",
                ["SBIN"] = "Banking",
                ["BHARTIARTL"] = "Telecom",
                ["KOTAKBANK"] = "Banking",
                ["LT"] = "Construction",
                ["ASIANPAINT"] = "Chemicals",
                ["MARUTI"] = "Automobile",
                ["TATAMOTORS"] = "Automobile",
                ["AXISBANK"] = "Banking",
                ["HCLTECH"] = "Technology",
                ["SUNPHARMA"] = "Pharmaceuticals",
                ["TITAN"] = "Consumer Goods",
                ["WIPRO"] = "Technology",
                ["ULTRACEMCO"] = "Construction Materials"
            };

            return sectors.TryGetValue(symbol, out var sector) ? sector : "Other";
        }

        private string GetIndustry(string symbol)
        {
            var industries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = "Oil & Gas",
                ["TCS"] = "IT Services",
                ["HDFCBANK"] = "Private Bank",
                ["INFY"] = "IT Services",
                ["ICICIBANK"] = "Private Bank",
                ["HINDUNILVR"] = "Consumer Goods",
                ["ITC"] = "Diversified",
                ["SBIN"] = "Public Bank",
                ["BHARTIARTL"] = "Telecom Services",
                ["KOTAKBANK"] = "Private Bank",
                ["LT"] = "Engineering & Construction",
                ["ASIANPAINT"] = "Paints & Coatings",
                ["MARUTI"] = "Automobile",
                ["TATAMOTORS"] = "Automobile",
                ["AXISBANK"] = "Private Bank",
                ["HCLTECH"] = "IT Services",
                ["SUNPHARMA"] = "Pharmaceuticals",
                ["TITAN"] = "Jewelry & Watches",
                ["WIPRO"] = "IT Services",
                ["ULTRACEMCO"] = "Cement"
            };

            return industries.TryGetValue(symbol, out var industry) ? industry : "General";
        }

        private void LoadFallbackStocks()
        {
            _logger.LogWarning("Loading fallback stock list");

            var fallbackSymbols = new List<string>
            {
                "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
                "HINDUNILVR", "ITC", "SBIN", "BHARTIARTL", "KOTAKBANK",
                "LT", "ASIANPAINT", "MARUTI", "TATAMOTORS", "AXISBANK",
                "HCLTECH", "SUNPHARMA", "TITAN", "WIPRO", "ULTRACEMCO"
            };

            var newStockList = new List<StockInfo>();
            var newFnoList = new List<string>();

            foreach (var symbol in fallbackSymbols)
            {
                newStockList.Add(new StockInfo
                {
                    Symbol = symbol,
                    CompanyName = GetCompanyName(symbol),
                    Sector = GetSector(symbol),
                    Industry = GetIndustry(symbol),
                    IsFNOSec = true,
                    Exchange = "NSE"
                });
                newFnoList.Add(symbol);
            }

            _cachedStocks = newStockList;
            _cachedFNOSymbols = newFnoList;
            _lastRefresh = DateTime.UtcNow;

            _logger.LogInformation("Loaded {Count} fallback stocks", _cachedStocks.Count);
        }

        #endregion
    }
}