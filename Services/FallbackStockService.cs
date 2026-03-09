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

        // Cooldown periods (in minutes)
        private const int ALPHA_VANTAGE_COOLDOWN = 24 * 60; // 24 hours
        private const int FREE_API_COOLDOWN = 1; // 1 minute
        private const int NSE_API_COOLDOWN = 0; // No cooldown for NSE (reliable)
        private const int YAHOO_COOLDOWN = 1; // 1 minute

        public FallbackStockService(
            IStockService primaryService,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<FallbackStockService> logger)
        {
            _primaryService = primaryService;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<List<StockData>> GetIndianStockDataAsync()
        {
            _logger.LogInformation("FallbackService.GetIndianStockDataAsync called");

            // STEP 1: ALWAYS try the primary service first (your StockService)
            try
            {
                _logger.LogInformation("Attempting to get stock list from primary service (StockService)");
                var stocks = await _primaryService.GetIndianStockDataAsync();

                if (stocks != null && stocks.Any())
                {
                    _logger.LogInformation("Primary service returned {Count} stocks successfully", stocks.Count);
                    return stocks;
                }

                _logger.LogWarning("Primary service returned no stocks");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primary service failed to get stock list");

                // Check if it's a rate limit issue
                if (ex.Message.Contains("rate limit") || ex.Message.Contains("25 requests"))
                {
                    MarkApiRateLimited("AlphaVantage", ALPHA_VANTAGE_COOLDOWN);
                }
            }

            // STEP 2: If primary fails, use fallback symbols from configuration
            _logger.LogInformation("Primary service failed, using fallback symbols from configuration");

            var symbols = _configuration.GetSection("StockApiSettings:IndianStocks").Get<List<string>>()
                ?? new List<string> {
            "RELIANCE.NS", "TCS.NS", "HDFCBANK.NS", "INFY.NS", "ICICIBANK.NS",
            "HINDUNILVR.NS", "ITC.NS", "SBIN.NS", "BHARTIARTL.NS", "KOTAKBANK.NS"
                };

            var results = new List<StockData>();

            foreach (var symbol in symbols)
            {
                try
                {
                    _logger.LogDebug("Fetching {Symbol} via fallback APIs", symbol);
                    var stock = await GetStockDataAsync(symbol); // This calls your individual stock fallback logic
                    if (stock != null)
                    {
                        results.Add(stock);
                    }
                    await Task.Delay(500); // Be respectful to APIs
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching {Symbol} in fallback", symbol);
                }
            }

            _logger.LogInformation("Fallback service returning {Count} stocks from fallback APIs", results.Count);
            return results;
        }
        //public async Task<List<StockData>> GetIndianStockDataAsync()
        //{
        //    var symbols = _configuration.GetSection("StockApiSettings:IndianStocks").Get<List<string>>()
        //        ?? new List<string> { "RELIANCE.NS", "TCS.NS", "HDFCBANK.NS", "INFY.NS", "ICICIBANK.NS" };

        //    var results = new List<StockData>();

        //    foreach (var symbol in symbols)
        //    {
        //        try
        //        {
        //            var stock = await GetStockDataAsync(symbol);
        //            if (stock != null)
        //            {
        //                results.Add(stock);
        //            }
        //            await Task.Delay(500); // Be respectful to APIs
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Error fetching {Symbol}", symbol);
        //        }
        //    }

        //    return results;
        //}

        public async Task<StockData?> GetStockDataAsync(string symbol)
        {
            // Clean the symbol
            var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Replace(".BSE", "");
            var isNSE = symbol.Contains(".NS") || symbol.Contains(".NSE") || !symbol.Contains(".BO");
            var isBSE = symbol.Contains(".BO") || symbol.Contains(".BSE");

            _logger.LogInformation("Fetching data for {Symbol} (NSE: {IsNSE}, BSE: {IsBSE})", symbol, isNSE, isBSE);

            // TRY 1: Primary Service (Alpha Vantage)
            if (!IsApiRateLimited("AlphaVantage"))
            {
                try
                {
                    _logger.LogInformation("Trying primary API (Alpha Vantage) for {Symbol}", symbol);
                    var stock = await _primaryService.GetStockDataAsync(symbol);

                    if (stock != null && stock.Price > 0)
                    {
                        _logger.LogInformation("Primary API successful for {Symbol}", symbol);
                        return stock;
                    }

                    // Check if rate limited by making a test call
                    var isRateLimited = await CheckAlphaVantageRateLimit();
                    if (isRateLimited)
                    {
                        MarkApiRateLimited("AlphaVantage", ALPHA_VANTAGE_COOLDOWN);
                        _logger.LogWarning("Alpha Vantage rate limit detected. Cooling down for 24 hours");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Primary API failed for {Symbol}", symbol);

                    if (ex.Message.Contains("rate limit") || ex.Message.Contains("25 requests"))
                    {
                        MarkApiRateLimited("AlphaVantage", ALPHA_VANTAGE_COOLDOWN);
                    }
                }
            }
            else
            {
                _logger.LogInformation("Alpha Vantage is in cooldown. Skipping...");
            }

            // TRY 2: NSE Direct API (Most Reliable)
            if (isNSE && !IsApiRateLimited("NSE"))
            {
                var nseData = await GetFromNSEAsync(cleanSymbol);
                if (nseData != null)
                {
                    _logger.LogInformation("NSE API successful for {Symbol}", symbol);
                    return nseData;
                }
            }

            // TRY 3: BSE Direct API
            if (isBSE && !IsApiRateLimited("BSE"))
            {
                var bseData = await GetFromBSEAsync(cleanSymbol);
                if (bseData != null)
                {
                    _logger.LogInformation("BSE API successful for {Symbol}", symbol);
                    return bseData;
                }
            }

            // TRY 4: Yahoo Finance (Final Fallback)
            if (!IsApiRateLimited("Yahoo"))
            {
                var yahooData = await GetFromYahooFinanceAsync(symbol);
                if (yahooData != null)
                {
                    _logger.LogInformation("Yahoo Finance successful for {Symbol}", symbol);
                    return yahooData;
                }
            }

            _logger.LogError("All APIs failed for {Symbol}", symbol);
            return null;
        }

        #region Rate Limit Management

        private async Task<bool> CheckAlphaVantageRateLimit()
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var apiKey = _configuration["StockApiSettings:AlphaVantageApiKey"];

                if (string.IsNullOrEmpty(apiKey))
                    return false;

                var testUrl = $"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol=RELIANCE.BSE&apikey={apiKey}";

                var response = await client.GetAsync(testUrl);
                var content = await response.Content.ReadAsStringAsync();

                return content.Contains("rate limit") || content.Contains("25 requests") || content.Contains("API key");
            }
            catch
            {
                return false;
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

                    // Cooldown expired, remove it
                    _apiRateLimits.Remove(apiName);
                }
                return false;
            }
        }

        private void MarkApiRateLimited(string apiName, int cooldownMinutes)
        {
            lock (_lockObject)
            {
                _apiRateLimits[apiName] = new RateLimitInfo
                {
                    ApiName = apiName,
                    CooldownUntil = DateTime.UtcNow.AddMinutes(cooldownMinutes),
                    LastFailureTime = DateTime.UtcNow,
                    FailureCount = _apiRateLimits.ContainsKey(apiName)
                        ? _apiRateLimits[apiName].FailureCount + 1
                        : 1
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

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                client.DefaultRequestHeaders.Add("Referer", "https://www.nseindia.com");

                //// First visit homepage to get cookies
                //var homeResponse = await client.GetAsync("https://www.nseindia.com");

                //if (!homeResponse.IsSuccessStatusCode)
                //{
                //    _logger.LogWarning("Failed to get NSE cookies");
                //    MarkApiRateLimited("NSE", FREE_API_COOLDOWN);
                //    return null;
                //}

                //// Small delay to ensure cookies are set
                //await Task.Delay(1000);

                var url = $"https://www.nseindia.com/api/quote-equity?symbol={symbol.ToUpper()}";
                _logger.LogDebug("NSE URL: {Url}", url);

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug("NSE Response received, length: {Length}", content.Length);

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var data = JsonSerializer.Deserialize<NSEQuoteResponse>(content, options);

                    if (data?.PriceInfo != null)
                    {
                        _logger.LogInformation("NSE API successful for {Symbol}", symbol);

                        // Get day high/low from intraDayHighLow
                        decimal dayHigh = data.PriceInfo.IntraDayHighLow?.Max ?? data.PriceInfo.LastPrice;
                        decimal dayLow = data.PriceInfo.IntraDayHighLow?.Min ?? data.PriceInfo.LastPrice;

                        // Get 52-week high/low from weekHighLow
                        decimal yearHigh = data.PriceInfo.WeekHighLow?.Max ?? dayHigh * 1.2m;
                        decimal yearLow = data.PriceInfo.WeekHighLow?.Min ?? dayLow * 0.8m;

                        // Parse timestamp
                        DateTime timestamp = DateTime.Now;
                        if (!string.IsNullOrEmpty(data.Metadata?.LastUpdateTime))
                        {
                            DateTime.TryParse(data.Metadata.LastUpdateTime.Replace("-", " "), out timestamp);
                        }

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
                            Volume = 0, // Volume not in this response
                            YearHigh = yearHigh,
                            YearLow = yearLow,
                            MarketCap = 0, // Not provided
                            PE = data.Metadata?.PdSymbolPe,
                            Sector = data.IndustryInfo?.Sector,
                            Industry = data.IndustryInfo?.Industry,
                            Timestamp = timestamp,
                            Isin = data.Info?.Isin
                        };
                    }
                    else
                    {
                        _logger.LogWarning("NSE API returned but PriceInfo was null for {Symbol}", symbol);
                    }
                }
                else
                {
                    _logger.LogWarning("NSE API returned status {StatusCode} for {Symbol}", response.StatusCode, symbol);
                    MarkApiRateLimited("NSE", FREE_API_COOLDOWN);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NSE API failed for {Symbol}", symbol);
                MarkApiRateLimited("NSE", FREE_API_COOLDOWN);
            }

            return null;
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

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Referer", "https://www.bseindia.com");

                var url = $"https://api.bseindia.com/BseIndiaAPI/api/StockReachData/w?scripcode={scripCode}";
                _logger.LogDebug("BSE URL: {Url}", url);

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug("BSE Response: {Content}", content);

                    if (!content.Contains("Error Code"))
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };

                        var data = JsonSerializer.Deserialize<BSEQuoteResponse>(content, options);
                        if (data != null)
                        {
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
                    }
                    else
                    {
                        _logger.LogWarning("BSE API returned error for {Symbol}: {Content}", symbol, content);
                    }
                }
                else
                {
                    _logger.LogWarning("BSE API returned status {StatusCode} for {Symbol}", response.StatusCode, symbol);
                }

                MarkApiRateLimited("BSE", FREE_API_COOLDOWN);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BSE API failed for {Symbol}", symbol);
                MarkApiRateLimited("BSE", FREE_API_COOLDOWN);
            }

            return null;
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

            return mapping.ContainsKey(symbol.ToUpper()) ? mapping[symbol.ToUpper()] : string.Empty;
        }

        #endregion

        #region Yahoo Finance Implementation

        private async Task<StockData?> GetFromYahooFinanceAsync(string symbol)
        {
            try
            {
                _logger.LogInformation("Trying Yahoo Finance for {Symbol}", symbol);

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?region=IN&lang=en-IN";
                _logger.LogDebug("Yahoo URL: {Url}", url);

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug("Yahoo Response received, length: {Length}", content.Length);

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var data = JsonSerializer.Deserialize<YahooFinanceResponse>(content, options);

                    var quote = data?.Chart?.Result?.FirstOrDefault()?.Meta;
                    if (quote != null)
                    {
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
                            DayHigh = quote.RegularMarketDayHigh ?? price * 1.02m,
                            DayLow = quote.RegularMarketDayLow ?? price * 0.98m,
                            Open = quote.RegularMarketOpen ?? price,
                            PreviousClose = prevClose,
                            Volume = quote.RegularMarketVolume ?? 0,
                            YearHigh = price * 1.2m,
                            YearLow = price * 0.8m,
                            Timestamp = DateTime.Now
                        };
                    }
                }
                else
                {
                    _logger.LogWarning("Yahoo Finance returned status {StatusCode} for {Symbol}", response.StatusCode, symbol);
                }

                MarkApiRateLimited("Yahoo", YAHOO_COOLDOWN);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yahoo Finance failed for {Symbol}", symbol);
                MarkApiRateLimited("Yahoo", YAHOO_COOLDOWN);
            }

            return null;
        }

        #endregion
    }    
}