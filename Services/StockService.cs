using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockNotificationApi.Services
{
    public class StockService : IStockService
    {
        private readonly IHttpClientFactory _httpClientFactory; // Changed from HttpClient
        private readonly IConfiguration _configuration;
        private readonly ILogger<StockService> _logger;
        private readonly StockApiSettings _stockApiSettings;
        private readonly IStockListService _stockListService;

        public StockService(
            IHttpClientFactory httpClientFactory, // Changed to IHttpClientFactory
            IConfiguration configuration,
            IStockListService stockListService,
            ILogger<StockService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _stockListService = stockListService;
            _logger = logger;
            _stockApiSettings = configuration.GetSection("StockApiSettings").Get<StockApiSettings>()
                ?? throw new ArgumentNullException("StockApiSettings not configured");
        }

        public async Task<List<StockData>> GetIndianStockDataAsync()
        {
            // Get top 20 stocks by market cap dynamically
            var topStocks = await _stockListService.GetTopStocksByMarketCapAsync(20);
            var stockDataList = new List<StockData>();

            foreach (var stock in topStocks)
            {
                try
                {
                    // Determine exchange (NSE or BSE)
                    var symbol = stock.Exchange == "BSE"
                        ? $"{stock.Symbol}.BO"
                        : $"{stock.Symbol}.NS";

                    var stockData = await GetStockDataAsync(symbol);
                    if (stockData != null)
                    {
                        stockDataList.Add(stockData);
                    }

                    await Task.Delay(1000); // Rate limiting
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error fetching data for {stock.Symbol}");
                }
            }

            return stockDataList;
        }

        public async Task<StockData?> GetStockDataAsync(string symbol)
        {
            _logger.LogInformation("Fetching data for {Symbol} with fallback", symbol);

            // Clean the symbol (remove any exchange suffixes)
            var cleanSymbol = symbol.Replace(".BSE", "").Replace(".NSE", "").Replace(".NS", "").Replace(".BO", "");

            // Determine which exchanges to try based on the symbol suffix
            var tryNSE = symbol.Contains(".NS") || symbol.Contains(".NSE") || !symbol.Contains(".BO");
            var tryBSE = symbol.Contains(".BO") || symbol.Contains(".BSE") || !symbol.Contains(".NS");

            StockData? stockData = null;

            // TRY 1: NSE API (if applicable)
            if (tryNSE)
            {
                _logger.LogInformation("Attempt 1: Trying NSE for {Symbol}", cleanSymbol);
                stockData = await GetFromNSEAsync(cleanSymbol);
                if (stockData != null)
                {
                    _logger.LogInformation("NSE successful for {Symbol}", cleanSymbol);
                    return stockData;
                }
                _logger.LogWarning("NSE failed for {Symbol}, trying next source...", cleanSymbol);
            }

            // TRY 2: BSE API (if applicable)
            if (tryBSE && stockData == null)
            {
                _logger.LogInformation("Attempt 2: Trying BSE for {Symbol}", cleanSymbol);
                stockData = await GetFromBSEAsync(cleanSymbol);
                if (stockData != null)
                {
                    _logger.LogInformation("BSE successful for {Symbol}", cleanSymbol);
                    return stockData;
                }
                _logger.LogWarning("BSE failed for {Symbol}, trying next source...", cleanSymbol);
            }

            // TRY 3: Yahoo Finance (universal fallback)
            if (stockData == null)
            {
                _logger.LogInformation("Attempt 3: Trying Yahoo Finance for {Symbol}", cleanSymbol);
                stockData = await GetFromYahooFinanceAsync(cleanSymbol);
                if (stockData != null)
                {
                    _logger.LogInformation("Yahoo Finance successful for {Symbol}", cleanSymbol);
                    return stockData;
                }
            }

            // TRY 4: Alpha Vantage (last resort)
            if (stockData == null)
            {
                _logger.LogInformation("Attempt 4: Trying Alpha Vantage for {Symbol}", cleanSymbol);
                stockData = await GetFromAlphaVantageAsync(symbol);
                if (stockData != null)
                {
                    _logger.LogInformation("Alpha Vantage successful for {Symbol}", cleanSymbol);
                    return stockData;
                }
            }

            _logger.LogError("All data sources failed for {Symbol}", cleanSymbol);
            return null;
        }

        #region NSE API Implementation

        private async Task<StockData?> GetFromNSEAsync(string symbol)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                client.DefaultRequestHeaders.Add("Referer", "https://www.nseindia.com");

                //// Get cookies first
                //var homeResponse = await client.GetAsync("https://www.nseindia.com");
                //if (!homeResponse.IsSuccessStatusCode)
                //{
                //    _logger.LogWarning("Failed to get NSE cookies");
                //    return null;
                //}

                //await Task.Delay(1000);

                var url = $"https://www.nseindia.com/api/quote-equity?symbol={symbol.ToUpper()}";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var data = System.Text.Json.JsonSerializer.Deserialize<NSEQuoteResponse>(content, options);

                    if (data?.PriceInfo != null)
                    {
                        // Calculate market cap if issued size is available
                        decimal marketCap = 0;
                        if (data.SecurityInfo?.IssuedSize > 0)
                        {
                            marketCap = (data.PriceInfo.LastPrice * data.SecurityInfo.IssuedSize.Value) / 10000000;
                        }

                        // Get day range
                        decimal dayHigh = data.PriceInfo.IntraDayHighLow?.Max ?? data.PriceInfo.LastPrice;
                        decimal dayLow = data.PriceInfo.IntraDayHighLow?.Min ?? data.PriceInfo.LastPrice;

                        // Get 52-week range
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
                            Name = data.Info?.CompanyName ?? GetCompanyName(symbol),
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
                            MarketCap = marketCap,
                            PE = data.Metadata?.PdSymbolPe,
                            Sector = data.IndustryInfo?.Sector ?? GetSector(symbol),
                            Industry = data.IndustryInfo?.Industry ?? GetIndustry(symbol),
                            Timestamp = timestamp,
                            Isin = data.Info?.Isin
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NSE API failed for {Symbol}", symbol);
            }
            return null;
        }

        #endregion

        #region BSE API Implementation

        private async Task<StockData?> GetFromBSEAsync(string symbol)
        {
            try
            {
                var scripCode = GetBSEScripCode(symbol);
                if (string.IsNullOrEmpty(scripCode))
                {
                    _logger.LogDebug("No BSE scrip code mapping for {Symbol}", symbol);
                    return null;
                }

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Referer", "https://www.bseindia.com");

                var url = $"https://api.bseindia.com/BseIndiaAPI/api/StockReachData/w?scripcode={scripCode}";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    if (!content.Contains("Error Code"))
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };

                        var data = System.Text.Json.JsonSerializer.Deserialize<BSEQuoteResponse>(content, options);

                        if (data != null)
                        {
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
                                MarketCap = data.MarketCap / 10000000, // Convert to Crores
                                PE = data.PE,
                                Timestamp = data.UpdatedAt == DateTime.MinValue ? DateTime.Now : data.UpdatedAt
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BSE API failed for {Symbol}", symbol);
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
                ["AXISBANK"] = "532155"
            };

            return mapping.ContainsKey(symbol) ? mapping[symbol] : string.Empty;
        }

        #endregion

        #region Yahoo Finance Implementation

        private async Task<StockData?> GetFromYahooFinanceAsync(string symbol)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}.NS?region=IN&lang=en-IN";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var data = System.Text.Json.JsonSerializer.Deserialize<YahooFinanceResponse>(content, options);

                    var quote = data?.Chart?.Result?.FirstOrDefault()?.Meta;
                    if (quote != null)
                    {
                        var price = quote.RegularMarketPrice ?? 0;
                        var prevClose = quote.PreviousClose ?? price;
                        var change = price - prevClose;
                        var changePercent = prevClose > 0 ? (change / prevClose) * 100 : 0;

                        return new StockData
                        {
                            Symbol = symbol,
                            Name = symbol,
                            Exchange = "NSE",
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yahoo Finance failed for {Symbol}", symbol);
            }
            return null;
        }

        #endregion

        #region Alpha Vantage Implementation (Last Resort)

        private async Task<StockData?> GetFromAlphaVantageAsync(string symbol)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var url = $"{_stockApiSettings.BaseUrl}?function=GLOBAL_QUOTE&symbol={symbol}&apikey={_stockApiSettings.AlphaVantageApiKey}";

                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();

                // Check for rate limit
                if (content.Contains("rate limit") || content.Contains("25 requests"))
                {
                    _logger.LogWarning("Alpha Vantage rate limit reached");
                    return null;
                }

                var json = JObject.Parse(content);
                var globalQuote = json["Global Quote"];

                if (globalQuote == null || !globalQuote.HasValues)
                {
                    return null;
                }

                // Parse change percent
                var changePercentStr = globalQuote["10. change percent"]?.ToString().Replace("%", "") ?? "0";
                decimal.TryParse(changePercentStr, out decimal changePercent);

                decimal price = decimal.TryParse(globalQuote["05. price"]?.ToString(), out var p) ? p : 0;

                return new StockData
                {
                    Symbol = symbol.Replace(".BSE", "").Replace(".NSE", ""),
                    Name = GetCompanyName(symbol),
                    Price = price,
                    Change = decimal.TryParse(globalQuote["09. change"]?.ToString(), out var change) ? change : 0,
                    ChangePercent = changePercent,
                    DayHigh = decimal.TryParse(globalQuote["03. high"]?.ToString(), out var high) ? high : 0,
                    DayLow = decimal.TryParse(globalQuote["04. low"]?.ToString(), out var low) ? low : 0,
                    Volume = long.TryParse(globalQuote["06. volume"]?.ToString(), out var volume) ? volume : 0,
                    Timestamp = DateTime.TryParse(globalQuote["07. latest trading day"]?.ToString(), out var timestamp) ? timestamp : DateTime.Now,
                    Exchange = symbol.Contains(".BSE") ? "BSE" : "NSE",
                    YearHigh = decimal.TryParse(globalQuote["03. high"]?.ToString(), out var yearHigh) ? yearHigh * 1.2m : price * 1.2m,
                    YearLow = decimal.TryParse(globalQuote["04. low"]?.ToString(), out var yearLow) ? yearLow * 0.8m : price * 0.8m,
                    Open = decimal.TryParse(globalQuote["02. open"]?.ToString(), out var open) ? open : price,
                    PreviousClose = decimal.TryParse(globalQuote["08. previous close"]?.ToString(), out var prevClose) ? prevClose : price,
                    MarketCap = 0,
                    Sector = GetSector(symbol),
                    Industry = GetIndustry(symbol)
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alpha Vantage failed for {Symbol}", symbol);
                return null;
            }
        }

        #endregion

        #region Helper Methods

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
                ["KOTAKBANK"] = "Banking"
            };

            var key = symbol.Replace(".BSE", "").Replace(".NSE", "");
            return sectors.ContainsKey(key) ? sectors[key] : "Other";
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
                ["BHARTIARTL"] = "Telecom",
                ["KOTAKBANK"] = "Private Bank"
            };

            var key = symbol.Replace(".BSE", "").Replace(".NSE", "");
            return industries.ContainsKey(key) ? industries[key] : "General";
        }

        private string GetCompanyName(string symbol)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = "Reliance Industries",
                ["TCS"] = "Tata Consultancy Services",
                ["HDFCBANK"] = "HDFC Bank",
                ["INFY"] = "Infosys",
                ["ICICIBANK"] = "ICICI Bank",
                ["HINDUNILVR"] = "Hindustan Unilever",
                ["ITC"] = "ITC Limited",
                ["SBIN"] = "State Bank of India",
                ["BHARTIARTL"] = "Bharti Airtel",
                ["KOTAKBANK"] = "Kotak Mahindra Bank"
            };

            var key = symbol.Replace(".BSE", "").Replace(".NSE", "");
            return names.ContainsKey(key) ? names[key] : key;
        }

        #endregion
    }
}