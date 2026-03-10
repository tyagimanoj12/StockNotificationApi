using StockNotificationApi.Models;
using System.Text.Json;
using System.Text;
using StockNotificationApi.Interfaces;
using System.Text.RegularExpressions;
using System.Xml;

namespace StockNotificationApi.Services
{
    public class NewsService : INewsService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NewsService> _logger;
        private readonly IConfiguration _configuration;

        // Constants
        private const int HTTP_TIMEOUT_SECONDS = 30;
        private const int MAX_NEWS_ITEMS = 10;
        private const int NEWS_BATCH_SIZE = 5;
        private const int RATE_LIMIT_DELAY_MS = 1000;
        private const int MAX_NEWS_AGE_DAYS = 1;
        private const int YAHOO_NEWS_COUNT = 5;

        private static readonly HashSet<string> PositiveWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "surge", "gain", "rise", "up", "positive", "bull", "bullish",
            "growth", "profit", "high", "good", "rally", "breakout", "upside"
        };

        private static readonly HashSet<string> NegativeWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "fall", "drop", "down", "negative", "bear", "bearish", "loss",
            "low", "poor", "decline", "crash", "plunge", "slump", "downturn"
        };

        public NewsService(
            IHttpClientFactory httpClientFactory,
            ILogger<NewsService> logger,
            IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<List<StockNews>> GetStockNewsAsync(string symbol, int days = 1)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Empty symbol provided");
                return new List<StockNews>();
            }

            if (days <= 0) days = MAX_NEWS_AGE_DAYS;

            var newsList = new List<StockNews>();

            try
            {
                _logger.LogInformation("Fetching news for symbol: {Symbol}", symbol);

                // Try multiple sources
                var tasks = new List<Task<List<StockNews>>>
                {
                    GetFromGoogleNewsAsync(symbol),
                    GetFromYahooFinanceNewsAsync(symbol)
                };

                var results = await Task.WhenAll(tasks);

                foreach (var result in results)
                {
                    if (result?.Any() == true)
                    {
                        newsList.AddRange(result);
                    }
                }

                // Filter by date and analyze sentiment
                var cutoff = DateTime.UtcNow.AddDays(-days);
                newsList = newsList
                    .Where(n => n != null && n.PublishedAt >= cutoff)
                    .DistinctBy(n => n.Title) // Remove duplicates by title
                    .ToList();

                foreach (var news in newsList)
                {
                    news.Sentiment = AnalyzeSentiment(news);

                    // Extract affected stocks from title
                    news.AffectedStocks = ExtractStockSymbols(news.Title);
                }

                _logger.LogInformation("Retrieved {Count} news items for {Symbol}", newsList.Count, symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching news for {Symbol}", symbol);
            }

            return newsList.Take(MAX_NEWS_ITEMS).ToList();
        }

        public async Task<List<StockNews>> GetTopMarketNewsAsync(int count = 10)
        {
            if (count <= 0) count = MAX_NEWS_ITEMS;

            var allNews = new List<StockNews>();

            try
            {
                _logger.LogInformation("Fetching top market news");

                var client = CreateHttpClient();
                var tasks = new List<Task<List<StockNews>>>();

                // Google News RSS for Indian market
                tasks.Add(GetGoogleMarketNewsAsync(client));

                // Moneycontrol headlines
                tasks.Add(GetMoneycontrolNewsAsync(client));

                var results = await Task.WhenAll(tasks);

                foreach (var result in results)
                {
                    if (result?.Any() == true)
                    {
                        allNews.AddRange(result);
                    }
                }

                // Remove duplicates and sort by date
                allNews = allNews
                    .Where(n => n != null)
                    .DistinctBy(n => n.Title)
                    .OrderByDescending(n => n.PublishedAt)
                    .ToList();

                // Extract affected stocks for each news item
                foreach (var news in allNews)
                {
                    news.AffectedStocks = ExtractStockSymbols(news.Title);
                    news.Impact = DetermineImpact(news.Title);
                }

                _logger.LogInformation("Retrieved {Count} market news items", allNews.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching market news");
            }

            return allNews.Take(count).ToList();
        }

        public async Task<Dictionary<string, List<StockNews>>> GetNewsForSymbolsAsync(List<string> symbols)
        {
            if (symbols == null || !symbols.Any())
            {
                _logger.LogWarning("No symbols provided");
                return new Dictionary<string, List<StockNews>>();
            }

            var result = new Dictionary<string, List<StockNews>>();

            for (int i = 0; i < symbols.Count; i += NEWS_BATCH_SIZE)
            {
                var batch = symbols.Skip(i).Take(NEWS_BATCH_SIZE).ToList();
                var tasks = batch.Select(s => GetStockNewsAsync(s, MAX_NEWS_AGE_DAYS));
                var newsResults = await Task.WhenAll(tasks);

                for (int j = 0; j < batch.Count; j++)
                {
                    if (newsResults[j]?.Any() == true)
                    {
                        result[batch[j]] = newsResults[j];
                    }
                }

                if (i + NEWS_BATCH_SIZE < symbols.Count)
                {
                    await Task.Delay(RATE_LIMIT_DELAY_MS);
                }
            }

            _logger.LogInformation("Retrieved news for {Count} symbols", result.Count);
            return result;
        }

        public async Task<string> GetNewsSummaryAsync(List<StockNews> news)
        {
            if (news == null || !news.Any())
                return "No significant news today.";

            var positive = news.Count(n => n?.Sentiment == "Positive");
            var negative = news.Count(n => n?.Sentiment == "Negative");
            var neutral = news.Count(n => n?.Sentiment == "Neutral");

            var sb = new StringBuilder();
            sb.AppendLine("📰 <b>Market News Summary</b>");
            sb.AppendLine($"Positive: {positive} | Negative: {negative} | Neutral: {neutral}\n");

            foreach (var item in news.Where(n => n != null).Take(5))
            {
                var emoji = GetSentimentEmoji(item.Sentiment);

                // Show FULL title - no truncation!
                sb.AppendLine($"{emoji} <b>{item.Symbol ?? "Market"}</b>: {item.Title}");

                // Show affected stocks if available
                if (item.AffectedStocks?.Any() == true)
                {
                    sb.AppendLine($"   📊 Affects: {string.Join(", ", item.AffectedStocks.Take(3))}");
                }

                // Show impact if available
                if (!string.IsNullOrEmpty(item.Impact))
                {
                    sb.AppendLine($"   {item.Impact}");
                }

                if (!string.IsNullOrEmpty(item.Summary))
                {
                    sb.AppendLine($"   <i>{TruncateText(item.Summary, 150)}</i>");
                }

                sb.AppendLine($"   📅 {item.PublishedAt:HH:mm} | {item.Source ?? "Unknown"}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        #region Private Methods

        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/rss+xml, application/xml, text/xml");
            client.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);
            return client;
        }

        private async Task<List<StockNews>> GetFromGoogleNewsAsync(string symbol)
        {
            var news = new List<StockNews>();

            try
            {
                var client = CreateHttpClient();
                var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(symbol + " stock")}&hl=en-IN&gl=IN&ceid=IN:en";

                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    news = ParseRSSContent(content, symbol, "Google News");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google News failed for {Symbol}", symbol);
            }

            return news;
        }

        private async Task<List<StockNews>> GetGoogleMarketNewsAsync(HttpClient client)
        {
            var news = new List<StockNews>();

            try
            {
                var url = "https://news.google.com/rss/search?q=indian+stock+market&hl=en-IN&gl=IN&ceid=IN:en";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    news = ParseRSSContent(content, "Market", "Google News");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Market News failed");
            }

            return news;
        }

        private async Task<List<StockNews>> GetMoneycontrolNewsAsync(HttpClient client)
        {
            var news = new List<StockNews>();

            try
            {
                var url = "https://www.moneycontrol.com/rss/latestnews.xml";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    news = ParseRSSContent(content, "Market", "Moneycontrol");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Moneycontrol News failed");
            }

            return news;
        }

        private async Task<List<StockNews>> GetFromYahooFinanceNewsAsync(string symbol)
        {
            var news = new List<StockNews>();

            try
            {
                var client = CreateHttpClient();
                var url = $"https://query1.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(symbol)}&newsCount={YAHOO_NEWS_COUNT}";

                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<YahooNewsResponse>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (data?.news != null)
                    {
                        foreach (var item in data.news.Where(n => n != null))
                        {
                            news.Add(new StockNews
                            {
                                Symbol = symbol,
                                Title = CleanHtml(item.title ?? "No title"),
                                Summary = CleanHtml(item.summary ?? ""),
                                Source = item.publisher ?? "Yahoo Finance",
                                PublishedAt = DateTimeOffset.FromUnixTimeSeconds(item.providerPublishTime).DateTime,
                                Url = item.link ?? "",
                                AffectedStocks = ExtractStockSymbols(item.title ?? "")
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Yahoo Finance News failed for {Symbol}", symbol);
            }

            return news;
        }

        private List<StockNews> ParseRSSContent(string rssContent, string symbol, string source)
        {
            var news = new List<StockNews>();

            if (string.IsNullOrEmpty(rssContent))
                return news;

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(rssContent);

                var items = doc.GetElementsByTagName("item");
                foreach (XmlNode item in items)
                {
                    var title = GetNodeValue(item, "title");
                    var link = GetNodeValue(item, "link");
                    var pubDate = GetNodeValue(item, "pubDate");
                    var description = GetNodeValue(item, "description");

                    if (!string.IsNullOrEmpty(title))
                    {
                        var cleanTitle = CleanHtml(title);
                        news.Add(new StockNews
                        {
                            Symbol = symbol,
                            Title = cleanTitle, // Keep FULL title
                            Summary = CleanHtml(description),
                            Source = source,
                            PublishedAt = TryParseDate(pubDate),
                            Url = link,
                            AffectedStocks = ExtractStockSymbols(cleanTitle),
                            Impact = DetermineImpact(cleanTitle)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing RSS for {Symbol}", symbol);
            }

            return news;
        }

        private string GetNodeValue(XmlNode parent, string tagName)
        {
            var node = parent.SelectSingleNode(tagName);
            return node?.InnerText?.Trim() ?? string.Empty;
        }

        private DateTime TryParseDate(string dateString)
        {
            if (DateTime.TryParse(dateString, out var date))
                return date;
            return DateTime.Now;
        }

        private string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            // Remove HTML tags
            var text = Regex.Replace(html, "<.*?>", string.Empty);
            // Decode HTML entities
            text = System.Net.WebUtility.HtmlDecode(text);
            // Normalize whitespace
            text = Regex.Replace(text, @"\s+", " ").Trim();

            return text;
        }

        private string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text ?? string.Empty;

            return text.Substring(0, maxLength - 3) + "...";
        }

        private List<string> ExtractStockSymbols(string title)
        {
            var symbols = new List<string>();

            // Common stock symbols to look for
            var stockKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RELIANCE"] = "RELIANCE",
                ["TCS"] = "TCS",
                ["HDFC Bank"] = "HDFCBANK",
                ["HDFCBANK"] = "HDFCBANK",
                ["INFY"] = "INFY",
                ["Infosys"] = "INFY",
                ["ICICI"] = "ICICIBANK",
                ["ICICIBANK"] = "ICICIBANK",
                ["ITC"] = "ITC",
                ["SBIN"] = "SBIN",
                ["SBI"] = "SBIN",
                ["BHARTI"] = "BHARTIARTL",
                ["Airtel"] = "BHARTIARTL",
                ["KOTAK"] = "KOTAKBANK",
                ["LT"] = "LT",
                ["Tata Motors"] = "TATAMOTORS",
                ["TATAMOTORS"] = "TATAMOTORS",
                ["IRCTC"] = "IRCTC",
                ["HCL"] = "HCLTECH",
                ["SUNPHARMA"] = "SUNPHARMA",
                ["TITAN"] = "TITAN",
                ["WIPRO"] = "WIPRO",
                ["KAYNES"] = "KAYNES",
                ["KPITTECH"] = "KPITTECH",
                ["MAPMYINDIA"] = "MAPMYINDIA",
                ["ANGELONE"] = "ANGELONE",
                ["PNBHOUSING"] = "PNBHOUSING"
            };

            foreach (var keyword in stockKeywords)
            {
                if (title.Contains(keyword.Key, StringComparison.OrdinalIgnoreCase))
                {
                    symbols.Add(keyword.Value);
                }
            }

            return symbols.Distinct().ToList();
        }

        private string DetermineImpact(string title)
        {
            if (title.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("plunge", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("bloodbath", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("tanks", StringComparison.OrdinalIgnoreCase))
                return "🔴 HIGH IMPACT";

            if (title.Contains("rally", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("surge", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("gain", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("rise", StringComparison.OrdinalIgnoreCase))
                return "🟢 POSITIVE";

            if (title.Contains("volatile", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("uncertainty", StringComparison.OrdinalIgnoreCase))
                return "🟡 MEDIUM IMPACT";

            return "⚪ GENERAL";
        }

        private string AnalyzeSentiment(StockNews news)
        {
            if (news == null) return "Neutral";

            var text = $"{news.Title} {news.Summary}".ToLower();

            var positiveCount = PositiveWords.Count(word => text.Contains(word));
            var negativeCount = NegativeWords.Count(word => text.Contains(word));

            if (positiveCount > negativeCount + 1) return "Positive";
            if (negativeCount > positiveCount + 1) return "Negative";
            return "Neutral";
        }

        private string GetSentimentEmoji(string? sentiment)
        {
            return sentiment?.ToLower() switch
            {
                "positive" => "🟢",
                "negative" => "🔴",
                _ => "⚪"
            };
        }

        #endregion
    }
}