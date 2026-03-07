using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

                var stockData = new StockData
                {
                    Symbol = symbol.Replace(".BSE", ""),
                    Name = GetCompanyName(symbol),
                    Price = decimal.Parse(globalQuote["05. price"]?.ToString() ?? "0"),
                    Change = decimal.Parse(globalQuote["09. change"]?.ToString() ?? "0"),
                    ChangePercent = decimal.Parse(globalQuote["10. change percent"]?.ToString().Replace("%", "") ?? "0"),
                    DayHigh = decimal.Parse(globalQuote["03. high"]?.ToString() ?? "0"),
                    DayLow = decimal.Parse(globalQuote["04. low"]?.ToString() ?? "0"),
                    Volume = long.Parse(globalQuote["06. volume"]?.ToString() ?? "0"),
                    Timestamp = DateTime.Parse(globalQuote["07. latest trading day"]?.ToString() ?? DateTime.Now.ToString()),
                    Exchange = symbol.Contains(".BSE") ? "BSE" : "NSE"
                };

                return stockData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching stock data for {symbol}");
                return null;
            }
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