using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;
using static StockNotificationApi.Services.MemoryCacheService;

namespace StockNotificationApi.Services
{
    public class FallbackStockService : IStockService
    {
        private readonly ILogger<FallbackStockService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ICacheService _cacheService;
        private readonly ICircuitBreakerService _circuitBreakerService;
        private readonly IStockService _primaryStockService;

        private const int CACHE_DURATION_MINUTES = 15;
        private const string CACHE_PREFIX_ALL_STOCKS = "all_stocks_data_fallback";
        private const string CACHE_PREFIX_STOCK = "stock_fallback";
        private const int HTTP_TIMEOUT_SECONDS = 30;

        public FallbackStockService(
            ILogger<FallbackStockService> logger,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ICacheService cacheService,
            ICircuitBreakerService circuitBreakerService,
            IStockService primaryStockService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            _circuitBreakerService = circuitBreakerService ?? throw new ArgumentNullException(nameof(circuitBreakerService));
            _primaryStockService = primaryStockService ?? throw new ArgumentNullException(nameof(primaryStockService));
        }

        public async Task<List<StockData>> GetIndianStockDataAsync()
        {
            _logger.LogInformation("FallbackService.GetIndianStockDataAsync called");

            return await _cacheService.GetOrSetAsync(CACHE_PREFIX_ALL_STOCKS, async () =>
            {
                _logger.LogInformation("Attempting to get stock list from primary service");

                try
                {
                    var stocks = await _primaryStockService.GetIndianStockDataAsync();
                    if (stocks != null && stocks.Any())
                    {
                        _logger.LogInformation("Primary service returned {Count} stocks successfully", stocks.Count);
                        return stocks;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Primary service failed, using fallback");
                }

                _logger.LogWarning("Returning fallback stocks");
                return GetFallbackStocks();
            }, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES)) ?? new List<StockData>();
        }

        public async Task<StockData?> GetStockDataAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return null;

            _logger.LogInformation("FallbackService.GetStockDataAsync called for {Symbol}", symbol);

            var cacheKey = $"{CACHE_PREFIX_STOCK}_{symbol}";

            return await _cacheService.GetOrSetAsync(cacheKey, async () =>
            {
                _logger.LogInformation("Cache miss for {Symbol}, trying primary service", symbol);

                try
                {
                    var stock = await _primaryStockService.GetStockDataAsync(symbol);
                    if (stock != null && stock.Price > 0)
                    {
                        _logger.LogInformation("Primary service returned data for {Symbol}", symbol);
                        return stock;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Primary service failed for {Symbol}, using fallback", symbol);
                }

                _logger.LogInformation("Using fallback data for {Symbol}", symbol);
                return await GetFallbackStockDataAsync(symbol);
            }, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
        }

        public async Task<List<StockData>> GetMultipleQuotesAsync(List<string> symbols)
        {
            if (symbols == null || !symbols.Any())
                return new List<StockData>();

            _logger.LogInformation("Fallback: Fetching quotes for {Count} symbols", symbols.Count);

            var results = new List<StockData>();
            var lockObj = new object();

            var tasks = symbols.Select(async symbol =>
            {
                var stock = await GetStockDataAsync(symbol);
                if (stock != null)
                {
                    lock (lockObj)
                    {
                        results.Add(stock);
                    }
                }
            });

            await Task.WhenAll(tasks);

            _logger.LogInformation("Fallback: Retrieved {Count} stocks", results.Count);
            return results;
        }

        public void ClearCache()
        {
            _logger.LogInformation("Fallback: Clearing cache");
            _cacheService.RemoveByPattern(CACHE_PREFIX_ALL_STOCKS);
            _cacheService.RemoveByPattern(CACHE_PREFIX_STOCK);
        }

        // FIX: Implement the async method required by the interface
        public async Task<CacheStatistics> GetCacheStatisticsAsync()
        {
            return await Task.FromResult(_cacheService.GetStatistics());
        }

        private List<StockData> GetFallbackStocks()
        {
            var fallbackSymbols = new List<string>
            {
                "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
                "HINDUNILVR", "ITC", "SBIN", "BHARTIARTL", "KOTAKBANK",
                "LT", "ASIANPAINT", "MARUTI", "TATAMOTORS", "AXISBANK",
                "HCLTECH", "SUNPHARMA", "TITAN", "WIPRO", "ULTRACEMCO",
                "BAJFINANCE", "ADANIPORTS", "NTPC", "POWERGRID", "ONGC"
            };

            var stocks = new List<StockData>();
            var now = DateTime.Now;

            foreach (var symbol in fallbackSymbols)
            {
                stocks.Add(new StockData
                {
                    Symbol = symbol,
                    Name = GetCompanyName(symbol),
                    Exchange = "NSE",
                    Price = GetFallbackPrice(symbol),
                    Change = 0,
                    ChangePercent = 0,
                    DayHigh = 0,
                    DayLow = 0,
                    Open = 0,
                    PreviousClose = 0,
                    Volume = 0,
                    YearHigh = 0,
                    YearLow = 0,
                    MarketCap = GetFallbackMarketCap(symbol),
                    PE = 0,
                    Sector = GetSector(symbol),
                    Industry = GetIndustry(symbol),
                    Timestamp = now
                });
            }

            return stocks;
        }

        private async Task<StockData?> GetFallbackStockDataAsync(string symbol)
        {
            var fallbackStocks = GetFallbackStocks();
            return await Task.FromResult(fallbackStocks.FirstOrDefault(s =>
                s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
                s.Symbol.Equals(symbol.Replace(".NS", "").Replace(".BO", ""), StringComparison.OrdinalIgnoreCase)));
        }

        private decimal GetFallbackPrice(string symbol)
        {
            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = 1407.80m,
                ["TCS"] = 2383.80m,
                ["HDFCBANK"] = 744.15m,
                ["INFY"] = 1256.80m,
                ["ICICIBANK"] = 812.45m,
                ["HINDUNILVR"] = 1856.30m,
                ["ITC"] = 315.25m,
                ["SBIN"] = 445.60m,
                ["BHARTIARTL"] = 560.75m,
                ["KOTAKBANK"] = 1923.50m,
                ["LT"] = 1980.30m,
                ["ASIANPAINT"] = 2465.80m,
                ["MARUTI"] = 7932.50m,
                ["TATAMOTORS"] = 3075.60m,
                ["AXISBANK"] = 689.45m,
                ["HCLTECH"] = 1125.90m,
                ["SUNPHARMA"] = 915.60m,
                ["TITAN"] = 2630.25m,
                ["WIPRO"] = 365.20m,
                ["ULTRACEMCO"] = 5689.40m
            };

            var cleanSymbol = symbol.Replace(".NS", "").Replace(".BO", "");
            return prices.TryGetValue(cleanSymbol, out var price) ? price : 100m;
        }

        private decimal GetFallbackMarketCap(string symbol)
        {
            var marketCaps = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = 1900000m,
                ["TCS"] = 850000m,
                ["HDFCBANK"] = 550000m,
                ["INFY"] = 520000m,
                ["ICICIBANK"] = 280000m,
                ["HINDUNILVR"] = 480000m,
                ["ITC"] = 290000m,
                ["SBIN"] = 400000m,
                ["BHARTIARTL"] = 340000m,
                ["KOTAKBANK"] = 380000m
            };

            var cleanSymbol = symbol.Replace(".NS", "").Replace(".BO", "");
            return marketCaps.TryGetValue(cleanSymbol, out var cap) ? cap : 10000m;
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

            var cleanSymbol = symbol.Replace(".NS", "").Replace(".BO", "");
            return names.TryGetValue(cleanSymbol, out var name) ? name : symbol;
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

            var cleanSymbol = symbol.Replace(".NS", "").Replace(".BO", "");
            return sectors.TryGetValue(cleanSymbol, out var sector) ? sector : "Other";
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

            var cleanSymbol = symbol.Replace(".NS", "").Replace(".BO", "");
            return industries.TryGetValue(cleanSymbol, out var industry) ? industry : "General";
        }
    }
}