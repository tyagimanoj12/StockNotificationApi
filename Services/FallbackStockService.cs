using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockNotificationApi.Services
{
    public class FallbackStockService : IStockService
    {
        private readonly IStockService _primaryService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FallbackStockService> _logger;

        // Rate limit tracking
        private static readonly Dictionary<string, RateLimitInfo> _apiRateLimits = new();
        private static readonly object _lockObject = new();

        // Constants
        private const int ALPHA_VANTAGE_COOLDOWN = 24 * 60; // 24 hours
        private const int FREE_API_COOLDOWN = 1; // 1 minute
        private const int NSE_API_COOLDOWN = 0; // No cooldown for NSE
        private const int YAHOO_COOLDOWN = 1; // 1 minute
        private const int API_RATE_LIMIT_DELAY_MS = 500;
        private const int HTTP_TIMEOUT_SECONDS = 30;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const decimal DEFAULT_YEAR_HIGH_MULTIPLIER = 1.2m;
        private const decimal DEFAULT_YEAR_LOW_MULTIPLIER = 0.8m;
        private const decimal DEFAULT_DAY_HIGH_MULTIPLIER = 1.02m;
        private const decimal DEFAULT_DAY_LOW_MULTIPLIER = 0.98m;

        private readonly HashSet<string> _rateLimitKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "rate limit", "25 requests", "API key", "too many requests", "rate limited"
        };

        public FallbackStockService(
            IStockService primaryService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<FallbackStockService> logger)
        {
            _primaryService = primaryService ?? throw new ArgumentNullException(nameof(primaryService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<StockData>> GetIndianStockDataAsync()
        {
            _logger.LogInformation("FallbackService.GetIndianStockDataAsync called");

            // Try primary service first
            var stocks = await TryGetFromPrimaryService();
            if (stocks != null)
                return stocks;

            // Fallback to configuration symbols
            return await GetFromFallbackSymbols();
        }

        private async Task<List<StockData>?> TryGetFromPrimaryService()
        {
            try
            {
                _logger.LogInformation("Attempting to get stock list from primary service");
                var stocks = await _primaryService.GetIndianStockDataAsync();

                if (stocks?.Any() == true)
                {
                    _logger.LogInformation("Primary service returned {Count} stocks successfully", stocks.Count);
                    return stocks;
                }

                _logger.LogWarning("Primary service returned no stocks");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primary service failed to get stock list");
                CheckForRateLimit(ex.Message);
            }

            return null;
        }

        private async Task<List<StockData>> GetFromFallbackSymbols()
        {
            _logger.LogInformation("Using fallback symbols from configuration");

            var symbols = GetFallbackSymbols();
            var results = new List<StockData>();

            foreach (var symbol in symbols)
            {
                try
                {
                    _logger.LogDebug("Fetching {Symbol} via fallback APIs", symbol);
                    var stock = await GetStockDataAsync(symbol);
                    if (stock != null)
                    {
                        results.Add(stock);
                    }
                    await Task.Delay(API_RATE_LIMIT_DELAY_MS);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching {Symbol} in fallback", symbol);
                }
            }

            _logger.LogInformation("Fallback service returning {Count} stocks", results.Count);
            return results;
        }

        private List<string> GetFallbackSymbols()
        {
            return _configuration.GetSection("StockApiSettings:IndianStocks").Get<List<string>>()
                ?? new List<string> {
                    "RELIANCE.NS", "TCS.NS", "HDFCBANK.NS", "INFY.NS", "ICICIBANK.NS",
                    "HINDUNILVR.NS", "ITC.NS", "SBIN.NS", "BHARTIARTL.NS", "KOTAKBANK.NS"
                };
        }

        public async Task<StockData?> GetStockDataAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Empty symbol provided");
                return null;
            }

            var (cleanSymbol, isNSE, isBSE) = ParseSymbol(symbol);
            _logger.LogInformation("Fetching data for {Symbol} (NSE: {IsNSE}, BSE: {IsBSE})", symbol, isNSE, isBSE);

            // Try primary service first
            var stock = await TryGetFromPrimaryServiceForSymbol(symbol);
            if (stock != null)
                return stock;

            // Try NSE
            if (isNSE)
            {
                stock = await TryGetFromNSE(cleanSymbol, symbol);
                if (stock != null)
                    return stock;
            }

            // Try BSE
            if (isBSE)
            {
                stock = await TryGetFromBSE(cleanSymbol, symbol);
                if (stock != null)
                    return stock;
            }

            // Try Yahoo as last resort
            stock = await TryGetFromYahoo(symbol);
            if (stock != null)
                return stock;

            _logger.LogError("All APIs failed for {Symbol}", symbol);
            return null;
        }

        private (string cleanSymbol, bool isNSE, bool isBSE) ParseSymbol(string symbol)
        {
            var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Replace(".BSE", "");
            var isNSE = symbol.Contains(".NS") || symbol.Contains(".NSE") || !symbol.Contains(".BO");
            var isBSE = symbol.Contains(".BO") || symbol.Contains(".BSE");

            return (cleanSymbol, isNSE, isBSE);
        }

        private async Task<StockData?> TryGetFromPrimaryServiceForSymbol(string symbol)
        {
            if (IsApiRateLimited("AlphaVantage"))
            {
                _logger.LogInformation("Alpha Vantage is in cooldown. Skipping...");
                return null;
            }

            try
            {
                _logger.LogInformation("Trying primary API (Alpha Vantage) for {Symbol}", symbol);
                var stock = await _primaryService.GetStockDataAsync(symbol);

                if (stock?.Price > 0)
                {
                    _logger.LogInformation("Primary API successful for {Symbol}", symbol);
                    return stock;
                }

                var isRateLimited = await CheckAlphaVantageRateLimit();
                if (isRateLimited)
                {
                    MarkApiRateLimited("AlphaVantage", ALPHA_VANTAGE_COOLDOWN);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primary API failed for {Symbol}", symbol);
                CheckForRateLimit(ex.Message);
            }

            return null;
        }

        private async Task<StockData?> TryGetFromNSE(string cleanSymbol, string originalSymbol)
        {
            if (IsApiRateLimited("NSE"))
                return null;

            var nseData = await GetFromNSEAsync(cleanSymbol);
            if (nseData != null)
            {
                _logger.LogInformation("NSE API successful for {Symbol}", originalSymbol);
                return nseData;
            }

            return null;
        }

        private async Task<StockData?> TryGetFromBSE(string cleanSymbol, string originalSymbol)
        {
            if (IsApiRateLimited("BSE"))
                return null;

            var bseData = await GetFromBSEAsync(cleanSymbol);
            if (bseData != null)
            {
                _logger.LogInformation("BSE API successful for {Symbol}", originalSymbol);
                return bseData;
            }

            return null;
        }

        private async Task<StockData?> TryGetFromYahoo(string symbol)
        {
            if (IsApiRateLimited("Yahoo"))
                return null;

            var yahooData = await GetFromYahooFinanceAsync(symbol);
            if (yahooData != null)
            {
                _logger.LogInformation("Yahoo Finance successful for {Symbol}", symbol);
                return yahooData;
            }

            return null;
        }

        #region Rate Limit Management

        private async Task<bool> CheckAlphaVantageRateLimit()
        {
            try
            {
                var apiKey = _configuration["StockApiSettings:AlphaVantageApiKey"];
                if (string.IsNullOrEmpty(apiKey))
                    return false;

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);

                var testUrl = $"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol=RELIANCE.BSE&apikey={apiKey}";
                var response = await client.GetAsync(testUrl);
                var content = await response.Content.ReadAsStringAsync();

                return IsRateLimitedResponse(content);
            }
            catch
            {
                return false;
            }
        }

        private bool IsRateLimitedResponse(string content)
        {
            return _rateLimitKeywords.Any(keyword =>
                content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private void CheckForRateLimit(string errorMessage)
        {
            if (IsRateLimitedResponse(errorMessage))
            {
                MarkApiRateLimited("AlphaVantage", ALPHA_VANTAGE_COOLDOWN);
            }
        }

        private bool IsApiRateLimited(string apiName)
        {
            lock (_lockObject)
            {
                if (_apiRateLimits.TryGetValue(apiName, out var limitInfo))
                {
                    if (DateTime.UtcNow < limitInfo.CooldownUntil)
                    {
                        var remaining = (limitInfo.CooldownUntil - DateTime.UtcNow).TotalMinutes;
                        _logger.LogDebug("API {ApiName} is in cooldown for {Remaining:F1} more minutes", apiName, remaining);
                        return true;
                    }

                    _apiRateLimits.Remove(apiName);
                }
                return false;
            }
        }

        private void MarkApiRateLimited(string apiName, int cooldownMinutes)
        {
            lock (_lockObject)
            {
                var failureCount = _apiRateLimits.ContainsKey(apiName)
                    ? _apiRateLimits[apiName].FailureCount + 1
                    : 1;

                _apiRateLimits[apiName] = new RateLimitInfo
                {
                    ApiName = apiName,
                    CooldownUntil = DateTime.UtcNow.AddMinutes(cooldownMinutes),
                    LastFailureTime = DateTime.UtcNow,
                    FailureCount = failureCount
                };

                _logger.LogWarning("API {ApiName} marked as rate limited until {CooldownUntil:yyyy-MM-dd HH:mm:ss}",
                    apiName, _apiRateLimits[apiName].CooldownUntil.ToLocalTime());
            }
        }

        #endregion

        #region NSE API Implementation

        private async Task<StockData?> GetFromNSEAsync(string symbol)
        {
            try
            {
                _logger.LogInformation("Trying NSE API for {Symbol}", symbol);

                var client = CreateHttpClientWithHeaders("https://www.nseindia.com");
                var url = $"https://www.nseindia.com/api/quote-equity?symbol={symbol.ToUpper()}";

                _logger.LogDebug("NSE URL: {Url}", url);

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    return await ParseNSEResponse(response, symbol);
                }

                _logger.LogWarning("NSE API returned status {StatusCode} for {Symbol}", response.StatusCode, symbol);
                MarkApiRateLimited("NSE", FREE_API_COOLDOWN);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NSE API failed for {Symbol}", symbol);
                MarkApiRateLimited("NSE", FREE_API_COOLDOWN);
            }

            return null;
        }

        private async Task<StockData?> ParseNSEResponse(HttpResponseMessage response, string symbol)
        {
            var content = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("NSE Response received, length: {Length}", content.Length);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var data = JsonSerializer.Deserialize<NSEQuoteResponse>(content, options);

            if (data?.PriceInfo == null)
            {
                _logger.LogWarning("NSE API returned but PriceInfo was null for {Symbol}", symbol);
                return null;
            }

            _logger.LogInformation("NSE API successful for {Symbol}", symbol);

            var (dayHigh, dayLow) = GetDayRange(data);
            var (yearHigh, yearLow) = GetYearRange(data, dayHigh, dayLow);
            var timestamp = ParseTimestamp(data.Metadata?.LastUpdateTime);

            return new StockData
            {
                Symbol = symbol,
                Name = data.Info?.CompanyName ?? symbol,
                Exchange = "NSE",
                Price = data.PriceInfo.LastPrice,
                Change = data.PriceInfo.Change,
                ChangePercent = data.PriceInfo.PChange,
                Open = data.PriceInfo.Open,
                DayHigh = dayHigh,
                DayLow = dayLow,
                PreviousClose = data.PriceInfo.PreviousClose,
                Volume = 0,
                YearHigh = yearHigh,
                YearLow = yearLow,
                MarketCap = 0,
                PE = data.Metadata?.PdSymbolPe,
                Sector = data.IndustryInfo?.Sector,
                Industry = data.IndustryInfo?.Industry,
                Timestamp = timestamp,
                Isin = data.Info?.Isin
            };
        }

        private (decimal dayHigh, decimal dayLow) GetDayRange(NSEQuoteResponse data)
        {
            var dayHigh = data.PriceInfo.IntraDayHighLow?.Max ?? data.PriceInfo.LastPrice;
            var dayLow = data.PriceInfo.IntraDayHighLow?.Min ?? data.PriceInfo.LastPrice;
            return (dayHigh, dayLow);
        }

        private (decimal yearHigh, decimal yearLow) GetYearRange(NSEQuoteResponse data, decimal dayHigh, decimal dayLow)
        {
            var yearHigh = data.PriceInfo.WeekHighLow?.Max ?? dayHigh * DEFAULT_YEAR_HIGH_MULTIPLIER;
            var yearLow = data.PriceInfo.WeekHighLow?.Min ?? dayLow * DEFAULT_YEAR_LOW_MULTIPLIER;
            return (yearHigh, yearLow);
        }

        private DateTime ParseTimestamp(string? lastUpdateTime)
        {
            if (string.IsNullOrEmpty(lastUpdateTime))
                return DateTime.Now;

            if (DateTime.TryParse(lastUpdateTime.Replace("-", " "), out var timestamp))
                return timestamp;

            return DateTime.Now;
        }

        #endregion

        #region BSE API Implementation

        private async Task<StockData?> GetFromBSEAsync(string symbol)
        {
            try
            {
                _logger.LogInformation("Trying BSE API for {Symbol}", symbol);

                var scripCode = GetBSEScripCode(symbol);
                if (string.IsNullOrEmpty(scripCode))
                {
                    _logger.LogWarning("No BSE scrip code mapping for {Symbol}", symbol);
                    return null;
                }

                var client = CreateHttpClientWithHeaders("https://www.bseindia.com");
                var url = $"https://api.bseindia.com/BseIndiaAPI/api/StockReachData/w?scripcode={scripCode}";

                _logger.LogDebug("BSE URL: {Url}", url);

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    return await ParseBSEResponse(response, symbol);
                }

                _logger.LogWarning("BSE API returned status {StatusCode} for {Symbol}", response.StatusCode, symbol);
                MarkApiRateLimited("BSE", FREE_API_COOLDOWN);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BSE API failed for {Symbol}", symbol);
                MarkApiRateLimited("BSE", FREE_API_COOLDOWN);
            }

            return null;
        }

        private async Task<StockData?> ParseBSEResponse(HttpResponseMessage response, string symbol)
        {
            var content = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("BSE Response: {Content}", content);

            if (content.Contains("Error Code"))
            {
                _logger.LogWarning("BSE API returned error for {Symbol}: {Content}", symbol, content);
                return null;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var data = JsonSerializer.Deserialize<BSEQuoteResponse>(content, options);
            if (data == null)
                return null;

            _logger.LogInformation("BSE API successful for {Symbol}", symbol);

            return new StockData
            {
                Symbol = symbol,
                Name = data.CompanyName ?? symbol,
                Exchange = "BSE",
                Price = data.CurrentPrice,
                Change = data.Change,
                ChangePercent = data.PercentChange,
                Open = data.Open,
                DayHigh = data.DayHigh,
                DayLow = data.DayLow,
                PreviousClose = data.PreviousClose,
                Volume = data.Volume,
                YearHigh = data.YearHigh,
                YearLow = data.YearLow,
                MarketCap = data.MarketCap,
                PE = data.PE,
                FaceValue = data.FaceValue,
                Industry = data.Industry,
                Timestamp = data.UpdatedAt == DateTime.MinValue ? DateTime.Now : data.UpdatedAt
            };
        }

        private string GetBSEScripCode(string symbol)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = "500325",
                ["TCS"] = "532540",
                ["HDFCBANK"] = "500180",
                ["INFY"] = "500209",
                ["ICICIBANK"] = "532174",
                ["HINDUNILVR"] = "500696",
                ["ITC"] = "500875",
                ["SBIN"] = "500112",
                ["BHARTIARTL"] = "532454",
                ["KOTAKBANK"] = "500247",
                ["LT"] = "500510",
                ["ASIANPAINT"] = "500820",
                ["MARUTI"] = "532500",
                ["TATAMOTORS"] = "500570",
                ["AXISBANK"] = "532155",
                ["HCLTECH"] = "500185",
                ["SUNPHARMA"] = "524715",
                ["TITAN"] = "500114",
                ["WIPRO"] = "507685",
                ["ULTRACEMCO"] = "532538",
                ["BAJFINANCE"] = "500034",
                ["ADANIPORTS"] = "532921",
                ["NTPC"] = "532555",
                ["POWERGRID"] = "532898",
                ["ONGC"] = "500312"
            };

            return mapping.TryGetValue(symbol.ToUpper(), out var code) ? code : string.Empty;
        }

        #endregion

        #region Yahoo Finance Implementation

        private async Task<StockData?> GetFromYahooFinanceAsync(string symbol)
        {
            try
            {
                _logger.LogInformation("Trying Yahoo Finance for {Symbol}", symbol);

                var client = CreateHttpClientWithHeaders();
                var cleanSymbol = symbol.Replace(".NS", "").Replace(".BO", "");
                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{cleanSymbol}.NS?region=IN&lang=en-IN";

                _logger.LogDebug("Yahoo URL: {Url}", url);

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    return await ParseYahooResponse(response, symbol);
                }

                _logger.LogWarning("Yahoo Finance returned status {StatusCode} for {Symbol}", response.StatusCode, symbol);
                MarkApiRateLimited("Yahoo", YAHOO_COOLDOWN);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yahoo Finance failed for {Symbol}", symbol);
                MarkApiRateLimited("Yahoo", YAHOO_COOLDOWN);
            }

            return null;
        }

        private async Task<StockData?> ParseYahooResponse(HttpResponseMessage response, string symbol)
        {
            var content = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Yahoo Response received, length: {Length}", content.Length);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var data = JsonSerializer.Deserialize<YahooFinanceResponse>(content, options);
            var quote = data?.Chart?.Result?.FirstOrDefault()?.Meta;

            if (quote == null)
                return null;

            _logger.LogInformation("Yahoo Finance successful for {Symbol}", symbol);

            var price = quote.RegularMarketPrice ?? 0;
            var prevClose = quote.PreviousClose ?? price;
            var change = price - prevClose;
            var changePercent = prevClose > 0 ? (change / prevClose) * 100 : 0;

            return new StockData
            {
                Symbol = symbol.Replace(".NS", "").Replace(".BO", ""),
                Name = symbol.Replace(".NS", "").Replace(".BO", ""),
                Exchange = symbol.Contains(".BO") ? "BSE" : "NSE",
                Price = price,
                Change = change,
                ChangePercent = changePercent,
                DayHigh = quote.RegularMarketDayHigh ?? price * DEFAULT_DAY_HIGH_MULTIPLIER,
                DayLow = quote.RegularMarketDayLow ?? price * DEFAULT_DAY_LOW_MULTIPLIER,
                Open = quote.RegularMarketOpen ?? price,
                PreviousClose = prevClose,
                Volume = quote.RegularMarketVolume ?? 0,
                YearHigh = price * DEFAULT_YEAR_HIGH_MULTIPLIER,
                YearLow = price * DEFAULT_YEAR_LOW_MULTIPLIER,
                Timestamp = DateTime.Now
            };
        }

        #endregion

        #region Helper Methods

        private HttpClient CreateHttpClientWithHeaders(string? referer = null)
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

            if (!string.IsNullOrEmpty(referer))
            {
                client.DefaultRequestHeaders.Add("Referer", referer);
            }

            client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);

            return client;
        }

        #endregion
    }
}