// Services/MoneycontrolService.cs
using HtmlAgilityPack;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StockNotificationApi.Services
{
    public class MoneycontrolService : IMoneycontrolService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MoneycontrolService> _logger;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly HttpClient _httpClient;

        // Moneycontrol endpoints
        private const string MC_SEARCH_URL = "https://www.moneycontrol.com/mc/mobi/search?q={0}";
        private const string MC_QUOTE_URL = "https://www.moneycontrol.com/india/stockpricequote/{0}/{1}";
        private const string MC_NEWS_URL = "https://www.moneycontrol.com/news/business/markets/";
        private const string MC_TOP_GAINERS_URL = "https://www.moneycontrol.com/stocks/marketstats/top-gainers-loser/nse/all.html";
        private const string MC_TOP_LOSERS_URL = "https://www.moneycontrol.com/stocks/marketstats/top-losers-loser/nse/all.html";
        private const string MC_INDICES_URL = "https://www.moneycontrol.com/indian-indices/";

        private static readonly Dictionary<string, string> SymbolToMcCode = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RELIANCE"] = "reliance-industries",
            ["TCS"] = "tata-consultancy-services",
            ["HDFCBANK"] = "hdfc-bank",
            ["INFY"] = "infosys",
            ["ICICIBANK"] = "icici-bank",
            ["HINDUNILVR"] = "hindustan-unilever",
            ["ITC"] = "itc",
            ["SBIN"] = "state-bank-of-india",
            ["BHARTIARTL"] = "bharti-airtel",
            ["KOTAKBANK"] = "kotak-mahindra-bank",
            ["LT"] = "larsen-and-toubro",
            ["ASIANPAINT"] = "asian-paints",
            ["MARUTI"] = "maruti-suzuki",
            ["TATAMOTORS"] = "tata-motors",
            ["AXISBANK"] = "axis-bank",
            ["HCLTECH"] = "hcl-technologies",
            ["SUNPHARMA"] = "sun-pharmaceutical-industries",
            ["TITAN"] = "titan-company",
            ["WIPRO"] = "wipro",
            ["ULTRACEMCO"] = "ultratech-cement",
            ["BAJFINANCE"] = "bajaj-finance",
            ["ADANIPORTS"] = "adani-ports-and-sez",
            ["NTPC"] = "ntpc",
            ["POWERGRID"] = "power-grid-corporation-india",
            ["ONGC"] = "oil-and-natural-gas-corporation"
        };

        public MoneycontrolService(
            IHttpClientFactory httpClientFactory,
            ILogger<MoneycontrolService> logger,
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

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");

            client.Timeout = TimeSpan.FromSeconds(15);

            return client;
        }

        public async Task<StockData?> GetQuoteAsync(string symbol)
        {
            return await _circuitBreaker.ExecuteAsync("Moneycontrol", async () =>
            {
                try
                {
                    var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Replace(".BSE", "").Trim();

                    // Get the Moneycontrol code for this symbol
                    if (!SymbolToMcCode.TryGetValue(cleanSymbol, out var mcCode))
                    {
                        // Try to search for the symbol
                        mcCode = await SearchSymbolAsync(cleanSymbol);
                        if (string.IsNullOrEmpty(mcCode))
                        {
                            _logger.LogWarning("No Moneycontrol mapping found for {Symbol}", cleanSymbol);
                            return null;
                        }
                    }

                    // Determine industry for URL (simplified - you might need a proper mapping)
                    var industry = GetIndustryForSymbol(cleanSymbol);

                    var url = string.Format(MC_QUOTE_URL, industry, mcCode);
                    _logger.LogDebug("Fetching Moneycontrol data from: {Url}", url);

                    var response = await _httpClient.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Moneycontrol returned {StatusCode} for {Symbol}", response.StatusCode, symbol);
                        return null;
                    }

                    var html = await response.Content.ReadAsStringAsync();

                    if (string.IsNullOrEmpty(html))
                    {
                        _logger.LogWarning("Empty response from Moneycontrol for {Symbol}", symbol);
                        return null;
                    }

                    return ParseMoneycontrolHtml(html, cleanSymbol);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching Moneycontrol data for {Symbol}", symbol);
                    throw;
                }
            }, null);
        }

        private async Task<string> SearchSymbolAsync(string symbol)
        {
            try
            {
                var url = string.Format(MC_SEARCH_URL, symbol);
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                var json = await response.Content.ReadAsStringAsync();

                // Parse search results
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var firstResult = root[0];
                    if (firstResult.TryGetProperty("sc_id", out var scId))
                    {
                        return scId.GetString() ?? string.Empty;
                    }
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private StockData? ParseMoneycontrolHtml(string html, string symbol)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Price
                var priceNode = doc.DocumentNode.SelectSingleNode("//span[@id='Nse_Prc_tick']") ??
                                doc.DocumentNode.SelectSingleNode("//div[@class='inprice1']//span") ??
                                doc.DocumentNode.SelectSingleNode("//span[@class='price']");

                if (priceNode == null)
                {
                    _logger.LogWarning("Could not find price element in Moneycontrol HTML for {Symbol}", symbol);
                    return null;
                }

                var priceText = priceNode.InnerText.Trim().Replace(",", "");
                if (!decimal.TryParse(priceText, out decimal price))
                {
                    _logger.LogWarning("Could not parse price '{PriceText}' for {Symbol}", priceText, symbol);
                    return null;
                }

                // Change
                var changeNode = doc.DocumentNode.SelectSingleNode("//span[@id='Nse_Change']") ??
                                 doc.DocumentNode.SelectSingleNode("//div[@class='change']//span");

                decimal change = 0;
                decimal changePercent = 0;

                if (changeNode != null)
                {
                    var changeText = changeNode.InnerText.Trim();
                    var match = Regex.Match(changeText, @"([+-]?\d+\.?\d*)\s*\(([+-]?\d+\.?\d*)%\)");

                    if (match.Success)
                    {
                        decimal.TryParse(match.Groups[1].Value, out change);
                        decimal.TryParse(match.Groups[2].Value, out changePercent);
                    }
                }

                // Company name
                var nameNode = doc.DocumentNode.SelectSingleNode("//h1[@class='company_name']") ??
                               doc.DocumentNode.SelectSingleNode("//h1[@itemprop='name']");

                var companyName = nameNode?.InnerText.Trim() ?? symbol;

                // Day high/low
                decimal dayHigh = price * 1.02m;
                decimal dayLow = price * 0.98m;

                var highNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Day High')]/following-sibling::td");
                if (highNode != null)
                {
                    var highText = highNode.InnerText.Trim().Replace(",", "");
                    decimal.TryParse(highText, out dayHigh);
                }

                var lowNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Day Low')]/following-sibling::td");
                if (lowNode != null)
                {
                    var lowText = lowNode.InnerText.Trim().Replace(",", "");
                    decimal.TryParse(lowText, out dayLow);
                }

                // Volume
                long volume = 0;
                var volumeNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Volume')]/following-sibling::td");
                if (volumeNode != null)
                {
                    var volumeText = volumeNode.InnerText.Trim().Replace(",", "");

                    if (volumeText.EndsWith("L"))
                        volume = (long)(decimal.Parse(volumeText.TrimEnd('L')) * 100000);
                    else if (volumeText.EndsWith("Cr"))
                        volume = (long)(decimal.Parse(volumeText.TrimEnd('C', 'r')) * 10000000);
                    else
                        long.TryParse(volumeText, out volume);
                }

                // 52 week high/low
                decimal yearHigh = price * 1.2m;
                decimal yearLow = price * 0.8m;

                var yearHighNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'52-Week High')]/following-sibling::td");
                if (yearHighNode != null)
                {
                    var highText = yearHighNode.InnerText.Trim().Replace(",", "");
                    decimal.TryParse(highText, out yearHigh);
                }

                var yearLowNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'52-Week Low')]/following-sibling::td");
                if (yearLowNode != null)
                {
                    var lowText = yearLowNode.InnerText.Trim().Replace(",", "");
                    decimal.TryParse(lowText, out yearLow);
                }

                // Open
                decimal open = price;
                var openNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Open')]/following-sibling::td");
                if (openNode != null)
                {
                    var openText = openNode.InnerText.Trim().Replace(",", "");
                    decimal.TryParse(openText, out open);
                }

                // Previous close
                decimal prevClose = price - change;
                var prevCloseNode = doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Prev Close')]/following-sibling::td");
                if (prevCloseNode != null)
                {
                    var prevCloseText = prevCloseNode.InnerText.Trim().Replace(",", "");
                    decimal.TryParse(prevCloseText, out prevClose);
                }

                return new StockData
                {
                    Symbol = symbol,
                    Name = companyName,
                    Exchange = "NSE",
                    Price = price,
                    Change = change,
                    ChangePercent = changePercent,
                    DayHigh = dayHigh,
                    DayLow = dayLow,
                    Volume = volume,
                    YearHigh = yearHigh,
                    YearLow = yearLow,
                    Open = open,
                    PreviousClose = prevClose,
                    Timestamp = DateTime.Now,
                    Sector = GetSectorFromSymbol(symbol),
                    Industry = GetIndustryFromSymbol(symbol)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Moneycontrol HTML for {Symbol}", symbol);
                return null;
            }
        }

        public async Task<List<StockNews>> GetStockNewsAsync(string symbol, int count = 10)
        {
            return await _circuitBreaker.ExecuteAsync("Moneycontrol", async () =>
            {
                var news = new List<StockNews>();

                try
                {
                    var cleanSymbol = symbol.Replace(".NS", "").Replace(".NSE", "").Trim();

                    // Moneycontrol news URL for specific stock
                    var url = $"https://www.moneycontrol.com/india/stockpricequote/news/{cleanSymbol}";

                    var response = await _httpClient.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                        return news;

                    var html = await response.Content.ReadAsStringAsync();

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var newsItems = doc.DocumentNode.SelectNodes("//div[@class='newsensebox']//li") ??
                                    doc.DocumentNode.SelectNodes("//ul[@class='newslist']//li");

                    if (newsItems != null)
                    {
                        foreach (var item in newsItems.Take(count))
                        {
                            var titleNode = item.SelectSingleNode(".//a");
                            var dateNode = item.SelectSingleNode(".//span[@class='date']");

                            if (titleNode != null)
                            {
                                var newsItem = new StockNews
                                {
                                    Symbol = symbol,
                                    Title = titleNode.InnerText.Trim(),
                                    Url = titleNode.GetAttributeValue("href", ""),
                                    Source = "Moneycontrol",
                                    PublishedAt = DateTime.Now, // Parse date if available
                                    AffectedStocks = new List<string> { symbol },
                                    Impact = "⚪ GENERAL",
                                    Category = "Company News"
                                };

                                // Try to parse date
                                if (dateNode != null)
                                {
                                    var dateText = dateNode.InnerText.Trim();
                                    if (DateTime.TryParse(dateText, out var pubDate))
                                        newsItem.PublishedAt = pubDate;
                                }

                                // Analyze sentiment
                                newsItem.Sentiment = AnalyzeSentiment(newsItem.Title);

                                news.Add(newsItem);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching Moneycontrol news for {Symbol}", symbol);
                }

                return news;
            }, new List<StockNews>());
        }

        public async Task<List<StockNews>> GetMarketNewsAsync(int count = 20)
        {
            return await _circuitBreaker.ExecuteAsync("Moneycontrol", async () =>
            {
                var news = new List<StockNews>();

                try
                {
                    var response = await _httpClient.GetAsync(MC_NEWS_URL);

                    if (!response.IsSuccessStatusCode)
                        return news;

                    var html = await response.Content.ReadAsStringAsync();

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    // Try multiple selectors for news items
                    var newsItems = doc.DocumentNode.SelectNodes("//div[@class='article_box']") ??
                                    doc.DocumentNode.SelectNodes("//li[@class='clearfix']") ??
                                    doc.DocumentNode.SelectNodes("//div[@class='content_wrapper']//li");

                    if (newsItems != null)
                    {
                        foreach (var item in newsItems.Take(count))
                        {
                            var titleNode = item.SelectSingleNode(".//h2//a") ??
                                           item.SelectSingleNode(".//p//a") ??
                                           item.SelectSingleNode(".//a[@class='article_title']");

                            if (titleNode == null)
                                continue;

                            var link = titleNode.GetAttributeValue("href", "");
                            var title = titleNode.InnerText.Trim();

                            // Skip if it's not a news article
                            if (string.IsNullOrEmpty(title) || title.Length < 10)
                                continue;

                            var newsItem = new StockNews
                            {
                                Symbol = "Market",
                                Title = title,
                                Url = link,
                                Source = "Moneycontrol",
                                PublishedAt = DateTime.Now, // Would need to parse date
                                AffectedStocks = ExtractStockSymbols(title),
                                Impact = DetermineImpact(title),
                                Category = "Market News"
                            };

                            // Try to get date
                            var dateNode = item.SelectSingleNode(".//span[@class='date']") ??
                                          item.SelectSingleNode(".//time") ??
                                          item.SelectSingleNode(".//div[@class='datetime']");

                            if (dateNode != null)
                            {
                                var dateText = dateNode.InnerText.Trim();
                                if (DateTime.TryParse(dateText, out var pubDate))
                                    newsItem.PublishedAt = pubDate;
                            }

                            // Analyze sentiment
                            newsItem.Sentiment = AnalyzeSentiment(title);

                            // Try to get summary
                            var summaryNode = item.SelectSingleNode(".//p[@class='desc']") ??
                                             item.SelectSingleNode(".//div[@class='summary']");

                            if (summaryNode != null)
                                newsItem.Summary = summaryNode.InnerText.Trim();

                            news.Add(newsItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching Moneycontrol market news");
                }

                return news;
            }, new List<StockNews>());
        }

        public async Task<Dictionary<string, decimal>> GetIndicesAsync()
        {
            return await _circuitBreaker.ExecuteAsync("Moneycontrol", async () =>
            {
                var indices = new Dictionary<string, decimal>();

                try
                {
                    var response = await _httpClient.GetAsync(MC_INDICES_URL);

                    if (!response.IsSuccessStatusCode)
                        return indices;

                    var html = await response.Content.ReadAsStringAsync();

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    // Nifty 50
                    var niftyNode = doc.DocumentNode.SelectSingleNode("//a[@href='/indian-indices/nifty-50-9']//span[@class='value']") ??
                                    doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Nifty 50')]/following-sibling::td");

                    if (niftyNode != null)
                    {
                        var valueText = niftyNode.InnerText.Trim().Replace(",", "");
                        if (decimal.TryParse(valueText, out decimal value))
                            indices["NIFTY 50"] = value;
                    }

                    // Sensex
                    var sensexNode = doc.DocumentNode.SelectSingleNode("//a[@href='/indian-indices/sensex-4']//span[@class='value']") ??
                                     doc.DocumentNode.SelectSingleNode("//td[contains(text(),'Sensex')]/following-sibling::td");

                    if (sensexNode != null)
                    {
                        var valueText = sensexNode.InnerText.Trim().Replace(",", "");
                        if (decimal.TryParse(valueText, out decimal value))
                            indices["SENSEX"] = value;
                    }

                    // Bank Nifty
                    var bankNiftyNode = doc.DocumentNode.SelectSingleNode("//a[@href='/indian-indices/bank-nifty-10']//span[@class='value']");

                    if (bankNiftyNode != null)
                    {
                        var valueText = bankNiftyNode.InnerText.Trim().Replace(",", "");
                        if (decimal.TryParse(valueText, out decimal value))
                            indices["BANK NIFTY"] = value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching Moneycontrol indices");
                }

                return indices;
            }, new Dictionary<string, decimal>());
        }

        public async Task<List<StockData>> GetTopGainersAsync(string category = "all")
        {
            return await GetMoversAsync(MC_TOP_GAINERS_URL, true);
        }

        public async Task<List<StockData>> GetTopLosersAsync(string category = "all")
        {
            return await GetMoversAsync(MC_TOP_LOSERS_URL, false);
        }

        private async Task<List<StockData>> GetMoversAsync(string url, bool isGainer)
        {
            return await _circuitBreaker.ExecuteAsync("Moneycontrol", async () =>
            {
                var stocks = new List<StockData>();

                try
                {
                    var response = await _httpClient.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                        return stocks;

                    var html = await response.Content.ReadAsStringAsync();

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var rows = doc.DocumentNode.SelectNodes("//table[@class='tblData']//tr[position()>1]") ??
                               doc.DocumentNode.SelectNodes("//div[@class='grybox']//tr");

                    if (rows != null)
                    {
                        foreach (var row in rows.Take(10))
                        {
                            var cells = row.SelectNodes(".//td");
                            if (cells == null || cells.Count < 5)
                                continue;

                            var symbol = cells[0].InnerText.Trim();
                            var priceText = cells[1].InnerText.Trim().Replace(",", "");
                            var changeText = cells[2].InnerText.Trim();
                            var percentText = cells[3].InnerText.Trim().Replace("%", "");

                            if (decimal.TryParse(priceText, out decimal price) &&
                                decimal.TryParse(percentText, out decimal changePercent))
                            {
                                // For gainers, changePercent is positive, for losers it's negative
                                if (!isGainer)
                                    changePercent = -Math.Abs(changePercent);

                                var change = (changePercent * price) / 100;

                                stocks.Add(new StockData
                                {
                                    Symbol = symbol,
                                    Name = symbol,
                                    Exchange = "NSE",
                                    Price = price,
                                    Change = change,
                                    ChangePercent = changePercent,
                                    DayHigh = price * 1.02m,
                                    DayLow = price * 0.98m,
                                    Volume = 0,
                                    Timestamp = DateTime.Now
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching movers from Moneycontrol");
                }

                return stocks;
            }, new List<StockData>());
        }

        #region Helper Methods

        private string GetIndustryForSymbol(string symbol)
        {
            var upperSymbol = symbol.ToUpper();

            return upperSymbol switch
            {
                "RELIANCE" => "oil-gas",
                "TCS" => "computers-software",
                "HDFCBANK" => "banks-private-sector",
                "INFY" => "computers-software",
                "ICICIBANK" => "banks-private-sector",
                "HINDUNILVER" => "fmcg",
                "ITC" => "cigarettes",
                "SBIN" => "banks-public-sector",
                "BHARTIARTL" => "telecommunications",
                "KOTAKBANK" => "banks-private-sector",
                "LT" => "engineering",
                "ASIANPAINT" => "paints-varnishes",
                "MARUTI" => "auto",
                "TATAMOTORS" => "auto",
                "AXISBANK" => "banks-private-sector",
                "HCLTECH" => "computers-software",
                "SUNPHARMA" => "pharmaceuticals",
                "TITAN" => "jewellery",
                "WIPRO" => "computers-software",
                "ULTRACEMCO" => "cement",
                "BAJFINANCE" => "finance",
                "ADANIPORTS" => "ports",
                "NTPC" => "power-generation",
                "POWERGRID" => "power-transmission",
                "ONGC" => "oil-exploration",
                _ => "other"
            };
        }

        private string GetSectorFromSymbol(string symbol)
        {
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
                "SUNPHARMA" => "Pharmaceuticals",
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
                "BHARTIARTL" => "Telecom Services",
                "KOTAKBANK" => "Banking",
                "LT" => "Engineering & Construction",
                "ASIANPAINT" => "Paints",
                "MARUTI" => "Automobile",
                "TATAMOTORS" => "Automobile",
                "AXISBANK" => "Banking",
                "HCLTECH" => "IT Services",
                "SUNPHARMA" => "Pharmaceuticals",
                "TITAN" => "Jewelry & Watches",
                "WIPRO" => "IT Services",
                "ULTRACEMCO" => "Cement",
                _ => "General"
            };
        }

        private List<string> ExtractStockSymbols(string title)
        {
            var symbols = new List<string>();

            var knownSymbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Reliance"] = "RELIANCE",
                ["TCS"] = "TCS",
                ["HDFC Bank"] = "HDFCBANK",
                ["Infosys"] = "INFY",
                ["ICICI Bank"] = "ICICIBANK",
                ["Hindustan Unilever"] = "HINDUNILVR",
                ["ITC"] = "ITC",
                ["SBI"] = "SBIN",
                ["Bharti Airtel"] = "BHARTIARTL",
                ["Kotak"] = "KOTAKBANK",
                ["L&T"] = "LT",
                ["Asian Paints"] = "ASIANPAINT",
                ["Maruti"] = "MARUTI",
                ["Tata Motors"] = "TATAMOTORS",
                ["Axis Bank"] = "AXISBANK",
                ["HCL"] = "HCLTECH",
                ["Sun Pharma"] = "SUNPHARMA",
                ["Titan"] = "TITAN",
                ["Wipro"] = "WIPRO",
                ["Ultratech"] = "ULTRACEMCO"
            };

            foreach (var kvp in knownSymbols)
            {
                if (title.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    symbols.Add(kvp.Value);
                }
            }

            return symbols.Distinct().ToList();
        }

        private string DetermineImpact(string title)
        {
            var lowerTitle = title.ToLower();

            if (lowerTitle.Contains("crash") || lowerTitle.Contains("plunge") ||
                lowerTitle.Contains("bloodbath") || lowerTitle.Contains("tanks"))
                return "🔴 HIGH IMPACT";

            if (lowerTitle.Contains("rally") || lowerTitle.Contains("surge") ||
                lowerTitle.Contains("soar") || lowerTitle.Contains("jump"))
                return "🟢 POSITIVE";

            if (lowerTitle.Contains("volatile") || lowerTitle.Contains("uncertainty") ||
                lowerTitle.Contains("cautious"))
                return "🟡 MEDIUM IMPACT";

            return "⚪ GENERAL";
        }

        private string AnalyzeSentiment(string title)
        {
            var positiveWords = new[] { "surge", "gain", "rise", "up", "positive", "bull", "bullish",
                                        "growth", "profit", "high", "good", "rally", "breakout" };

            var negativeWords = new[] { "fall", "drop", "down", "negative", "bear", "bearish",
                                        "loss", "low", "poor", "decline", "crash", "plunge" };

            var lowerTitle = title.ToLower();

            var positiveCount = positiveWords.Count(w => lowerTitle.Contains(w));
            var negativeCount = negativeWords.Count(w => lowerTitle.Contains(w));

            if (positiveCount > negativeCount + 1)
                return "Positive";
            if (negativeCount > positiveCount + 1)
                return "Negative";

            return "Neutral";
        }

        #endregion
    }
}