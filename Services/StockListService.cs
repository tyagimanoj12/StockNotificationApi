using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text.Json;

namespace StockNotificationApi.Services
{
    public class StockListService : IStockListService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<StockListService> _logger;
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;

        // Constants
        private const int LARGE_CAP_THRESHOLD = 20000;
        private const int MID_CAP_THRESHOLD = 5000;
        private const int DEFAULT_STOCK_COUNT = 200;
        private const int PROCESSING_BATCH_SIZE = 20;
        private const int API_RATE_LIMIT_DELAY_MS = 200;
        private const int CACHE_DURATION_HOURS = 6;
        private const int FNO_LIST_SIZE = 100;
        private const int HTTP_TIMEOUT_SECONDS = 30;

        private static List<StockInfo> _cachedNSEStocks = new();
        private static List<string> _cachedFNOSymbols = new();
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(CACHE_DURATION_HOURS);

        public StockListService(
            IHttpClientFactory httpClientFactory,
            ILogger<StockListService> logger,
            IMemoryCache cache,
            IServiceScopeFactory scopeFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        public async Task<List<StockInfo>> GetNSEStocksAsync()
        {
            // Return cached list if not expired
            if (_cachedNSEStocks.Any() && DateTime.UtcNow - _lastRefresh < RefreshInterval)
            {
                _logger.LogInformation("Returning {Count} cached NSE stocks", _cachedNSEStocks.Count);
                return new List<StockInfo>(_cachedNSEStocks); // Return a copy to prevent modification
            }

            await RefreshStockListAsync();
            return new List<StockInfo>(_cachedNSEStocks);
        }

        public async Task<List<StockInfo>> GetBSEStocksAsync()
        {
            _logger.LogWarning("BSE API not yet implemented");
            return new List<StockInfo>();
        }

        public async Task<List<StockInfo>> GetTopStocksByMarketCapAsync(int count = 50)
        {
            if (count <= 0)
            {
                _logger.LogWarning("Invalid count requested: {Count}", count);
                return new List<StockInfo>();
            }

            var stocksWithCap = await GetStocksWithMarketCapAsync(DEFAULT_STOCK_COUNT);
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
            if (count <= 0)
            {
                _logger.LogWarning("Invalid count requested: {Count}", count);
                return new List<StockInfo>();
            }

            _logger.LogInformation("Getting stocks with market cap data...");

            // Check cache first
            var cacheKey = "StocksWithMarketCap";
            if (_cache.TryGetValue(cacheKey, out List<StockInfo>? cached) && cached != null)
            {
                _logger.LogInformation("Returning {Count} cached stocks with market cap", cached.Count);
                return cached.Take(count).ToList();
            }

            // Get all stock symbols
            var allStocks = await GetNSEStocksAsync();
            if (!allStocks.Any())
            {
                _logger.LogWarning("No NSE stocks available for enrichment");
                return new List<StockInfo>();
            }

            var enrichedStocks = new List<StockInfo>();
            _logger.LogInformation("Enriching {Total} stocks with market cap data...", allStocks.Count);

            int processed = 0;
            foreach (var stock in allStocks.Take(DEFAULT_STOCK_COUNT))
            {
                try
                {
                    if (stock?.Symbol == null) continue;

                    // Create a scope to get StockService
                    using var scope = _scopeFactory.CreateScope();
                    var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                    // Try NSE first
                    var stockData = await stockService.GetStockDataAsync($"{stock.Symbol}.NS");

                    if (stockData != null && stockData.MarketCap > 0)
                    {
                        var enrichedStock = new StockInfo
                        {
                            Symbol = stock.Symbol,
                            CompanyName = stockData.Name,
                            Exchange = "NSE",
                            Sector = stockData.Sector,
                            Industry = stockData.Industry,
                            MarketCap = stockData.MarketCap,
                            IsFNOSec = IsLikelyFNOSymbol(stock.Symbol)
                        };
                        enrichedStocks.Add(enrichedStock);
                    }

                    processed++;
                    if (processed % PROCESSING_BATCH_SIZE == 0)
                    {
                        _logger.LogInformation("Processed {Processed}/{Total} stocks", processed, allStocks.Count);
                    }

                    // Rate limiting
                    await Task.Delay(API_RATE_LIMIT_DELAY_MS);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get market cap for {Symbol}", stock?.Symbol);
                }
            }

            // Sort by market cap descending
            var sortedStocks = enrichedStocks
                .Where(s => s.MarketCap > 0)
                .OrderByDescending(s => s.MarketCap)
                .ToList();

            _logger.LogInformation("Successfully enriched {Count} stocks with market cap data", sortedStocks.Count);

            // Cache for 6 hours
            _cache.Set(cacheKey, sortedStocks, TimeSpan.FromHours(CACHE_DURATION_HOURS));

            return sortedStocks.Take(count).ToList();
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

            if (_cachedFNOSymbols.Any() && DateTime.UtcNow - _lastRefresh < RefreshInterval)
            {
                return _cachedFNOSymbols.Take(count).ToList();
            }

            await RefreshStockListAsync();
            return _cachedFNOSymbols.Take(count).ToList();
        }

        public async Task RefreshStockListAsync()
        {
            try
            {
                _logger.LogInformation("Refreshing stock list from NSE master quote API...");

                var client = _httpClientFactory.CreateClient();
                ConfigureHttpClient(client);

                _logger.LogInformation("Fetching master quote list...");
                var url = "https://www.nseindia.com/api/master-quote";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    await ProcessMasterQuoteResponse(content);
                    return;
                }

                _logger.LogWarning("Failed to get master quote. Status code: {StatusCode}", response.StatusCode);
                LoadFallbackStocks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing stock list");
                LoadFallbackStocks();
            }
        }

        #region Private Methods

        private void ConfigureHttpClient(HttpClient client)
        {
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("Referer", "https://www.nseindia.com");
            client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);
        }

        private async Task ProcessMasterQuoteResponse(string content)
        {
            try
            {
                var symbols = JsonSerializer.Deserialize<List<string>>(content);

                if (symbols != null && symbols.Any())
                {
                    var newStockList = new List<StockInfo>();
                    foreach (var symbol in symbols)
                    {
                        newStockList.Add(new StockInfo
                        {
                            Symbol = symbol,
                            CompanyName = GetCompanyName(symbol),
                            Exchange = "NSE",
                            Sector = GetSector(symbol),
                            Industry = GetIndustry(symbol),
                            IsFNOSec = IsLikelyFNOSymbol(symbol)
                        });
                    }

                    _cachedNSEStocks = newStockList;
                    _cachedFNOSymbols = symbols.Take(FNO_LIST_SIZE).ToList();
                    _lastRefresh = DateTime.UtcNow;

                    _logger.LogInformation("Successfully loaded {Count} stocks from NSE master quote", _cachedNSEStocks.Count);
                }
                else
                {
                    _logger.LogWarning("No symbols found in master quote response");
                    LoadFallbackStocks();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse master quote response");
                LoadFallbackStocks();
            }
        }

        private bool IsLikelyFNOSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

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
            if (string.IsNullOrEmpty(symbol)) return symbol;

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
            if (string.IsNullOrEmpty(symbol)) return "Other";

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
            if (string.IsNullOrEmpty(symbol)) return "General";

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

            _cachedNSEStocks = newStockList;
            _cachedFNOSymbols = newFnoList;
            _lastRefresh = DateTime.UtcNow;

            _logger.LogInformation("Loaded {Count} fallback stocks", _cachedNSEStocks.Count);
        }

        #endregion
    }
}