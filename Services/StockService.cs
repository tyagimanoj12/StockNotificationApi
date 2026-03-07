using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class StockService : IStockService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StockService> _logger;
        private readonly StockApiSettings _stockApiSettings;

        public StockService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<StockService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _stockApiSettings = configuration.GetSection("StockApiSettings").Get<StockApiSettings>()
                ?? throw new ArgumentNullException("StockApiSettings not configured");
        }

        public async Task<List<StockData>> GetIndianStockDataAsync()
        {
            var stockDataList = new List<StockData>();

            foreach (var stock in _stockApiSettings.IndianStocks)
            {
                try
                {
                    var stockData = await GetStockDataAsync(stock);
                    if (stockData != null)
                    {
                        stockDataList.Add(stockData);
                    }

                    // Rate limiting - Alpha Vantage allows 5 calls per minute for free tier
                    await Task.Delay(12000); // 12 seconds delay between calls
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error fetching data for {stock}");
                }
            }

            return stockDataList;
        }

        // Services/StockService.cs - Update the GetStockDataAsync method
        public async Task<StockData?> GetStockDataAsync(string symbol)
        {
            try
            {
                var url = $"{_stockApiSettings.BaseUrl}?function=GLOBAL_QUOTE&symbol={symbol}&apikey={_stockApiSettings.AlphaVantageApiKey}";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                var globalQuote = json["Global Quote"];
                if (globalQuote == null || !globalQuote.HasValues)
                {
                    _logger.LogWarning($"No data found for {symbol}");
                    return null;
                }

                // Parse change percent
                var changePercentStr = globalQuote["10. change percent"]?.ToString().Replace("%", "") ?? "0";
                decimal changePercent = 0;
                decimal.TryParse(changePercentStr, out changePercent);

                var stockData = new StockData
                {
                    Symbol = symbol.Replace(".BSE", "").Replace(".NSE", ""),
                    Name = GetCompanyName(symbol),
                    Price = decimal.TryParse(globalQuote["05. price"]?.ToString(), out var price) ? price : 0,
                    Change = decimal.TryParse(globalQuote["09. change"]?.ToString(), out var change) ? change : 0,
                    ChangePercent = changePercent,
                    DayHigh = decimal.TryParse(globalQuote["03. high"]?.ToString(), out var high) ? high : 0,
                    DayLow = decimal.TryParse(globalQuote["04. low"]?.ToString(), out var low) ? low : 0,
                    Volume = long.TryParse(globalQuote["06. volume"]?.ToString(), out var volume) ? volume : 0,
                    Timestamp = DateTime.TryParse(globalQuote["07. latest trading day"]?.ToString(), out var timestamp) ? timestamp : DateTime.Now,
                    Exchange = symbol.Contains(".BSE") ? "BSE" : "NSE",

                    // Initialize new properties with defaults or calculate them
                    YearHigh = decimal.TryParse(globalQuote["03. high"]?.ToString(), out var yearHigh) ? yearHigh * 1.2m : price * 1.2m, // Approximate if not available
                    YearLow = decimal.TryParse(globalQuote["04. low"]?.ToString(), out var yearLow) ? yearLow * 0.8m : price * 0.8m,
                    Open = decimal.TryParse(globalQuote["02. open"]?.ToString(), out var open) ? open : price,
                    PreviousClose = decimal.TryParse(globalQuote["08. previous close"]?.ToString(), out var prevClose) ? prevClose : price,
                    PE = null, // Alpha Vantage doesn't provide PE in this endpoint
                    MarketCap = 0, // Would need another endpoint
                    Sector = GetSector(symbol),
                    Industry = GetIndustry(symbol)
                };

                return stockData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching stock data for {symbol}");
                return null;
            }
        }

        // Add helper methods
        private string GetSector(string symbol)
        {
            var sectors = new Dictionary<string, string>
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
            var industries = new Dictionary<string, string>
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
            // Simplified company name mapping
            var names = new Dictionary<string, string>
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
    }
}