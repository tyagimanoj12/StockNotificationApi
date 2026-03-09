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

        private static List<StockInfo> _cachedNSEStocks = new();
        private static List<string> _cachedFNOSymbols = new();
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6); // Refresh every 6 hours
        private readonly IServiceScopeFactory _scopeFactory; // Add this


        public StockListService(
            IHttpClientFactory httpClientFactory,
            ILogger<StockListService> logger,
            IMemoryCache cache,
            IServiceScopeFactory scopeFactory)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _cache = cache;
            _scopeFactory = scopeFactory;
        }

        public async Task<List<StockInfo>> GetNSEStocksAsync()
        {
            // Return cached list if not expired
            if (_cachedNSEStocks.Any() && DateTime.UtcNow - _lastRefresh < RefreshInterval)
            {
                _logger.LogInformation("Returning {Count} cached NSE stocks", _cachedNSEStocks.Count);
                return _cachedNSEStocks;
            }

            await RefreshStockListAsync();
            return _cachedNSEStocks;
        }

        public async Task<List<StockInfo>> GetBSEStocksAsync()
        {
            // For now, return empty list or you can implement BSE later
            _logger.LogWarning("BSE API not yet implemented");
            return new List<StockInfo>();
        }

        //public async Task<List<StockInfo>> GetTopStocksByMarketCapAsync(int count = 50)
        //{
        //    var allStocks = await GetNSEStocksAsync();

        //    // Return the first 'count' stocks (the list is ordered by trading activity)
        //    return allStocks
        //        .Where(s => !string.IsNullOrEmpty(s.Symbol))
        //        .Take(count)
        //        .ToList();
        //}

        //public async Task<List<StockInfo>> GetTopStocksByMarketCapAsync(int count = 50)
        //{
        //    // Try to get from cache first
        //    var cacheKey = "TopStocksByMarketCap";
        //    if (_cache.TryGetValue(cacheKey, out List<StockInfo>? cached) && cached != null)
        //    {
        //        return cached.Take(count).ToList();
        //    }

        //    // Get enriched stocks with market cap data
        //    var enrichedStocks = await GetStocksWithMarketCapAsync(200);

        //    // Cache for 6 hours
        //    _cache.Set(cacheKey, enrichedStocks, TimeSpan.FromHours(6));

        //    return enrichedStocks.Take(count).ToList();
        //}
        public async Task<List<StockInfo>> GetTopStocksByMarketCapAsync(int count = 50)
        {
            var stocksWithCap = await GetStocksWithMarketCapAsync(200);
            return stocksWithCap.Take(count).ToList();
        }
        public async Task<List<StockInfo>> GetLargeCapStocksAsync(int count = 20)
        {
            var stocks = await GetStocksWithMarketCapAsync(200);
            return stocks.Where(s => s.MarketCap >= 20000).Take(count).ToList();
        }

        public async Task<List<StockInfo>> GetMidCapStocksAsync(int count = 20)
        {
            var stocks = await GetStocksWithMarketCapAsync(200);
            return stocks.Where(s => s.MarketCap >= 5000 && s.MarketCap < 20000).Take(count).ToList();
        }

        public async Task<List<StockInfo>> GetSmallCapStocksAsync(int count = 20)
        {
            var stocks = await GetStocksWithMarketCapAsync(200);
            return stocks.Where(s => s.MarketCap < 5000).Take(count).ToList();
        }

        public async Task<Dictionary<string, List<StockInfo>>> GetAllCategoriesAsync()
        {
            var stocks = await GetStocksWithMarketCapAsync(200);

            return new Dictionary<string, List<StockInfo>>
            {
                ["LargeCap"] = stocks.Where(s => s.MarketCap >= 20000).ToList(),
                ["MidCap"] = stocks.Where(s => s.MarketCap >= 5000 && s.MarketCap < 20000).ToList(),
                ["SmallCap"] = stocks.Where(s => s.MarketCap < 5000).ToList()
            };
        }
        // In StockListService.cs
        public async Task<List<StockInfo>> GetStocksWithMarketCapAsync(int count = 100)
        {
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
            var enrichedStocks = new List<StockInfo>();

            _logger.LogInformation("Enriching {Total} stocks with market cap data...", allStocks.Count);

            int processed = 0;
            foreach (var stock in allStocks.Take(200)) // Process top 200
            {
                try
                {
                    // Create a scope to get StockService
                    using var scope = _scopeFactory.CreateScope();
                    var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                    // Try NSE first
                    var stockData = await stockService.GetStockDataAsync($"{stock.Symbol}.NS");

                    if (stockData != null && stockData.MarketCap > 0)
                    {
                        stock.MarketCap = stockData.MarketCap;
                        stock.Sector = stockData.Sector;
                        stock.Industry = stockData.Industry;
                        stock.CompanyName = stockData.Name;
                        enrichedStocks.Add(stock);
                    }

                    processed++;
                    if (processed % 20 == 0)
                    {
                        _logger.LogInformation("Processed {Processed}/{Total} stocks", processed, allStocks.Count);
                    }

                    // Rate limiting - don't overwhelm the API
                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get market cap for {Symbol}", stock.Symbol);
                }
            }

            // Sort by market cap descending
            var sortedStocks = enrichedStocks
                .Where(s => s.MarketCap > 0)
                .OrderByDescending(s => s.MarketCap)
                .ToList();

            _logger.LogInformation("Successfully enriched {Count} stocks with market cap data", sortedStocks.Count);

            // Cache for 6 hours
            _cache.Set(cacheKey, sortedStocks, TimeSpan.FromHours(6));

            return sortedStocks.Take(count).ToList();
        }
        public async Task<List<StockInfo>> GetStocksBySectorAsync(string sector)
        {
            var allStocks = await GetNSEStocksAsync();
            return allStocks.Where(s => s.Sector?.Contains(sector, StringComparison.OrdinalIgnoreCase) == true).ToList();
        }

        public async Task<List<string>> GetFNOSymbolsAsync(int count = 50)
        {
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
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                client.DefaultRequestHeaders.Add("Referer", "https://www.nseindia.com");

                //// First get cookies from homepage
                //_logger.LogDebug("Getting NSE cookies...");
                //var homeResponse = await client.GetAsync("https://www.nseindia.com");

                //if (!homeResponse.IsSuccessStatusCode)
                //{
                //    _logger.LogWarning("Failed to get NSE cookies, using fallback");
                //    LoadFallbackStocks();
                //    return;
                //}

                // Small delay to ensure cookies are set
                //await Task.Delay(2000);

                // Get the master quote list
                _logger.LogInformation("Fetching master quote list...");
                var url = "https://www.nseindia.com/api/master-quote";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    // Parse the JSON array directly to List<string>
                    var symbols = JsonSerializer.Deserialize<List<string>>(content);

                    if (symbols != null && symbols.Any())
                    {
                        _cachedNSEStocks = symbols.Select(s => new StockInfo
                        {
                            Symbol = s,
                            CompanyName = GetCompanyName(s), // Enrich with company name
                            Exchange = "NSE",
                            Sector = GetSector(s),
                            Industry = GetIndustry(s),
                            IsFNOSec = IsLikelyFNOSymbol(s) // Mark likely F&O stocks
                        }).ToList();

                        _logger.LogInformation("Successfully loaded {Count} stocks from NSE master quote", _cachedNSEStocks.Count);

                        // Store top 100 symbols for F&O list
                        _cachedFNOSymbols = symbols.Take(100).ToList();

                        _lastRefresh = DateTime.UtcNow;
                        return;
                    }
                }

                _logger.LogWarning("Failed to get master quote, using fallback");
                LoadFallbackStocks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing stock list");
                LoadFallbackStocks();
            }
        }

        #region Helper Methods

        private bool IsLikelyFNOSymbol(string symbol)
        {
            // This is a simplified check - you can expand this list
            var fnoCandidates = new HashSet<string>
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

            return names.ContainsKey(symbol) ? names[symbol] : symbol;
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

            return sectors.ContainsKey(symbol) ? sectors[symbol] : "Other";
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

            return industries.ContainsKey(symbol) ? industries[symbol] : "General";
        }

        private void LoadFallbackStocks()
        {
            _logger.LogWarning("Loading fallback stock list");

            _cachedNSEStocks = new List<StockInfo>();
            _cachedFNOSymbols = new List<string>();

            var fallbackSymbols = new List<string>
            {
                "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
                "HINDUNILVR", "ITC", "SBIN", "BHARTIARTL", "KOTAKBANK",
                "LT", "ASIANPAINT", "MARUTI", "TATAMOTORS", "AXISBANK",
                "HCLTECH", "SUNPHARMA", "TITAN", "WIPRO", "ULTRACEMCO"
            };

            foreach (var symbol in fallbackSymbols)
            {
                _cachedNSEStocks.Add(new StockInfo
                {
                    Symbol = symbol,
                    CompanyName = GetCompanyName(symbol),
                    Sector = GetSector(symbol),
                    Industry = GetIndustry(symbol),
                    IsFNOSec = true,
                    Exchange = "NSE"
                });

                _cachedFNOSymbols.Add(symbol);
            }

            _logger.LogInformation("Loaded {Count} fallback stocks", _cachedNSEStocks.Count);
            _lastRefresh = DateTime.UtcNow;
        }

        #endregion
    }
}