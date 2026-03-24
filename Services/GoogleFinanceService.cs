// Services/GoogleFinanceService.cs
using HtmlAgilityPack;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;
using System.Web;

namespace StockNotificationApi.Services
{
    public class GoogleFinanceService : IGoogleFinanceService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GoogleFinanceService> _logger;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly HttpClient _httpClient;

        // Google Finance endpoints
        private const string GOOGLE_FINANCE_URL = "https://www.google.com/finance/quote/{0}:{1}";
        private const string GOOGLE_FINANCE_MARKET_URL = "https://www.google.com/finance/markets/indexes";
        private const string GOOGLE_FINANCE_CURRENCY_URL = "https://www.google.com/finance/quote/{0}-{1}";

        // User agents to rotate
        private static readonly string[] UserAgents = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        };

        private static int _userAgentIndex = 0;

        public GoogleFinanceService(
            IHttpClientFactory httpClientFactory,
            ILogger<GoogleFinanceService> logger,
            ICircuitBreakerService circuitBreaker)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _circuitBreaker = circuitBreaker;
            _httpClient = CreateHttpClient();
        }

        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient();

            // Rotate user agent to avoid detection
            var userAgent = UserAgents[Interlocked.Increment(ref _userAgentIndex) % UserAgents.Length];

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");

            client.Timeout = TimeSpan.FromSeconds(15);

            return client;
        }

        public async Task<StockData?> GetQuoteAsync(string symbol)
        {
            return await _circuitBreaker.ExecuteAsync("GoogleFinance", async () =>
            {
                try
                {
                    // Clean symbol
                    var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Replace(".BSE", "").Trim();

                    // Determine exchange
                    var exchange = symbol.Contains(".BO") || symbol.Contains(".BSE") ? "BSE" : "NSE";

                    var url = string.Format(GOOGLE_FINANCE_URL, cleanSymbol, exchange);
                    _logger.LogDebug("Fetching Google Finance data from: {Url}", url);

                    var response = await _httpClient.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Google Finance returned {StatusCode} for {Symbol}", response.StatusCode, symbol);
                        return null;
                    }

                    var html = await response.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(html))
                    {
                        _logger.LogWarning("Empty response from Google Finance for {Symbol}", symbol);
                        return null;
                    }

                    return ParseGoogleFinanceHtml(html, cleanSymbol, exchange);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "HTTP error fetching Google Finance data for {Symbol}", symbol);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error fetching Google Finance data for {Symbol}", symbol);
                    throw;
                }
            }, null);
        }

        private StockData? ParseGoogleFinanceHtml(string html, string symbol, string exchange)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Google Finance has multiple possible structures - try different selectors

                // Method 1: Main price element
                // Add these additional selectors:
                var priceNode = doc.DocumentNode.SelectSingleNode("//div[@class='YMlKec fxKbKc']") ??                 // Main price
                                doc.DocumentNode.SelectSingleNode("//div[@jsname='vWLAgc']") ??                        // JS attribute (new)
                                doc.DocumentNode.SelectSingleNode("//div[@data-role='price']") ??                      // Data attribute
                                doc.DocumentNode.SelectSingleNode("//div[contains(@class,'price')]//span") ??          // Class contains price
                                doc.DocumentNode.SelectSingleNode("//span[@class='DFlfde']") ??                        // Common class
                                doc.DocumentNode.SelectSingleNode("//div[@class='kf1m0']");                            // Old class

                if (priceNode == null)
                {
                    _logger.LogWarning("Could not find price element in Google Finance HTML for {Symbol}", symbol);
                    return null;
                }

                var priceText = priceNode.InnerText.Trim().Replace(",", "").Replace("₹", "").Replace("$", "");
                if (!decimal.TryParse(priceText, out decimal price))
                {
                    _logger.LogWarning("Could not parse price '{PriceText}' for {Symbol}", priceText, symbol);
                    return null;
                }

                // Get company name
                var nameNode = doc.DocumentNode.SelectSingleNode("//div[@class='zzDege']") ??
                               doc.DocumentNode.SelectSingleNode("//h1[@class='KY7mAb']");

                var companyName = nameNode?.InnerText.Trim() ?? symbol;

                // Get change percentage
                var changeNode = doc.DocumentNode.SelectSingleNode("//div[@class='JwB6zf']") ??
                                 doc.DocumentNode.SelectSingleNode("//span[@class='IsqQVc NprOob']");

                decimal changePercent = 0;
                decimal change = 0;

                if (changeNode != null)
                {
                    var changeText = changeNode.InnerText.Trim();
                    // Format like "+0.35%" or "-1.23%"
                    if (changeText.Contains('%'))
                    {
                        var percentText = changeText.Replace("%", "").Replace("+", "").Replace(",", "");
                        if (decimal.TryParse(percentText, out decimal parsedPercent))
                        {
                            changePercent = parsedPercent;

                            // If it starts with + or is green, it's positive
                            if (changeText.StartsWith("-"))
                                changePercent = -changePercent;
                        }
                    }
                }

                // Get day range
                decimal dayHigh = price * 1.02m;
                decimal dayLow = price * 0.98m;

                var rangeNode = doc.DocumentNode.SelectSingleNode("//div[@class='P6K39c']");
                if (rangeNode != null)
                {
                    var rangeText = rangeNode.InnerText;
                    var parts = rangeText.Split('-');
                    if (parts.Length == 2)
                    {
                        if (decimal.TryParse(parts[0].Trim().Replace("₹", "").Replace(",", ""), out decimal low))
                            dayLow = low;
                        if (decimal.TryParse(parts[1].Trim().Replace("₹", "").Replace(",", ""), out decimal high))
                            dayHigh = high;
                    }
                }

                // Get volume
                long volume = 0;
                var volumeNode = doc.DocumentNode.SelectSingleNode("//div[contains(text(),'Volume')]/following-sibling::div") ??
                                 doc.DocumentNode.SelectSingleNode("//div[contains(@class,'volume')]");

                if (volumeNode != null)
                {
                    var volumeText = volumeNode.InnerText.Replace(",", "").Replace("M", "000000").Replace("K", "000");
                    long.TryParse(volumeText, out volume);
                }

                return new StockData
                {
                    Symbol = symbol,
                    Name = companyName,
                    Exchange = exchange,
                    Price = price,
                    Change = change,
                    ChangePercent = changePercent,
                    DayHigh = dayHigh,
                    DayLow = dayLow,
                    Volume = volume,
                    PreviousClose = price - change,
                    Open = price, // Google doesn't always show open
                    Timestamp = DateTime.Now,
                    Sector = GetSectorFromSymbol(symbol),
                    Industry = GetIndustryFromSymbol(symbol)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Google Finance HTML for {Symbol}", symbol);
                return null;
            }
        }

        public async Task<List<StockData>> GetMarketMoversAsync(string exchange = "NSE")
        {
            return await _circuitBreaker.ExecuteAsync("GoogleFinance", async () =>
            {
                var movers = new List<StockData>();

                try
                {
                    // This would need to be adapted based on Google Finance's market movers page
                    // For now, return empty list
                    _logger.LogInformation("Market movers from Google Finance not yet implemented");

                    return movers;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching market movers from Google Finance");
                    return movers;
                }
            }, new List<StockData>());
        }

        public async Task<Dictionary<string, decimal>> GetCurrencyRatesAsync()
        {
            return await _circuitBreaker.ExecuteAsync("GoogleFinance", async () =>
            {
                var rates = new Dictionary<string, decimal>();

                try
                {
                    // Get USD/INR rate
                    var usdInr = await GetCurrencyRateAsync("USD", "INR");
                    if (usdInr > 0)
                        rates["USD/INR"] = usdInr;

                    // Get EUR/INR rate
                    var eurInr = await GetCurrencyRateAsync("EUR", "INR");
                    if (eurInr > 0)
                        rates["EUR/INR"] = eurInr;

                    // Get GBP/INR rate
                    var gbpInr = await GetCurrencyRateAsync("GBP", "INR");
                    if (gbpInr > 0)
                        rates["GBP/INR"] = gbpInr;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching currency rates");
                }

                return rates;
            }, new Dictionary<string, decimal>());
        }

        private async Task<decimal> GetCurrencyRateAsync(string from, string to)
        {
            try
            {
                var url = string.Format(GOOGLE_FINANCE_CURRENCY_URL, from, to);
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return 0;

                var html = await response.Content.ReadAsStringAsync();

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var priceNode = doc.DocumentNode.SelectSingleNode("//div[@class='YMlKec fxKbKc']");

                if (priceNode != null)
                {
                    var priceText = priceNode.InnerText.Trim().Replace(",", "");
                    if (decimal.TryParse(priceText, out decimal price))
                        return price;
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching currency rate {From}/{To}", from, to);
                return 0;
            }
        }

        private string GetSectorFromSymbol(string symbol)
        {
            // This could be enhanced with a proper mapping or API
            var upperSymbol = symbol.ToUpper();

            return upperSymbol switch
            {
                "RELIANCE" => "Energy",
                "TCS" => "Technology",
                "HDFCBANK" => "Banking",
                "INFY" => "Technology",
                "ICICIBANK" => "Banking",
                "HINDUNILVR" => "FMCG",
                "ITC" => "FMCG",
                "SBIN" => "Banking",
                "BHARTIARTL" => "Telecom",
                "KOTAKBANK" => "Banking",
                "LT" => "Engineering",
                "ASIANPAINT" => "Chemicals",
                "MARUTI" => "Automobile",
                "TATAMOTORS" => "Automobile",
                "AXISBANK" => "Banking",
                "HCLTECH" => "Technology",
                "SUNPHARMA" => "Pharma",
                "TITAN" => "Consumer Goods",
                "WIPRO" => "Technology",
                "ULTRACEMCO" => "Cement",
                _ => "Other"
            };
        }

        private string GetIndustryFromSymbol(string symbol)
        {
            var upperSymbol = symbol.ToUpper();

            return upperSymbol switch
            {
                "RELIANCE" => "Oil & Gas",
                "TCS" => "IT Services",
                "HDFCBANK" => "Banking",
                "INFY" => "IT Services",
                "ICICIBANK" => "Banking",
                "HINDUNILVR" => "Consumer Goods",
                "ITC" => "Diversified",
                "SBIN" => "Banking",
                "BHARTIARTL" => "Telecom",
                "KOTAKBANK" => "Banking",
                _ => "General"
            };
        }
    }
}