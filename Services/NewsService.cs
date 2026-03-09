using StockNotificationApi.Models;
using System.Text.Json;
using System.Text;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Services
{
    public class NewsService : INewsService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NewsService> _logger;
        private readonly IConfiguration _configuration;

        public NewsService(
            IHttpClientFactory httpClientFactory,
            ILogger<NewsService> logger,
            IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<List<StockNews>> GetStockNewsAsync(string symbol, int days = 1)
        {
            var newsList = new List<StockNews>();

            try
            {
                // Try Google News RSS (free, no API key)
                var googleNews = await GetFromGoogleNewsAsync(symbol);
                if (googleNews.Any())
                {
                    newsList.AddRange(googleNews);
                }

                // Try Yahoo Finance News
                var yahooNews = await GetFromYahooFinanceNewsAsync(symbol);
                if (yahooNews.Any())
                {
                    newsList.AddRange(yahooNews);
                }

                // Filter by date
                var cutoff = DateTime.UtcNow.AddDays(-days);
                newsList = newsList.Where(n => n.PublishedAt >= cutoff).ToList();

                // Analyze sentiment
                foreach (var news in newsList)
                {
                    news.Sentiment = AnalyzeSentiment(news.Title + " " + news.Summary);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching news for {Symbol}", symbol);
            }

            return newsList.Take(10).ToList();
        }

        public async Task<List<StockNews>> GetTopMarketNewsAsync(int count = 10)
        {
            var allNews = new List<StockNews>();

            try
            {
                // Get general market news
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                // Google News RSS for Indian market
                var url = "https://news.google.com/rss/search?q=indian+stock+market&hl=en-IN&gl=IN&ceid=IN:en";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var news = ParseGoogleRSS(content, "Market");
                    allNews.AddRange(news);
                }

                // Moneycontrol headlines (alternative)
                url = "https://www.moneycontrol.com/rss/latestnews.xml";
                response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var news = ParseRSS(content, "Market");
                    allNews.AddRange(news);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching market news");
            }

            return allNews.Take(count).ToList();
        }

        public async Task<Dictionary<string, List<StockNews>>> GetNewsForSymbolsAsync(List<string> symbols)
        {
            var result = new Dictionary<string, List<StockNews>>();
            var batchSize = 5;

            for (int i = 0; i < symbols.Count; i += batchSize)
            {
                var batch = symbols.Skip(i).Take(batchSize).ToList();
                var tasks = batch.Select(s => GetStockNewsAsync(s, 1));
                var newsResults = await Task.WhenAll(tasks);

                for (int j = 0; j < batch.Count; j++)
                {
                    if (newsResults[j].Any())
                    {
                        result[batch[j]] = newsResults[j];
                    }
                }

                await Task.Delay(1000); // Rate limiting
            }

            return result;
        }

        public async Task<string> GetNewsSummaryAsync(List<StockNews> news)
        {
            if (!news.Any()) return "No significant news today.";

            var positive = news.Count(n => n.Sentiment == "Positive");
            var negative = news.Count(n => n.Sentiment == "Negative");
            var neutral = news.Count(n => n.Sentiment == "Neutral");

            var sb = new StringBuilder();
            sb.AppendLine($"📰 <b>Market News Summary</b>");
            sb.AppendLine($"Positive: {positive} | Negative: {negative} | Neutral: {neutral}\n");

            foreach (var item in news.Take(5))
            {
                var emoji = item.Sentiment == "Positive" ? "🟢" : item.Sentiment == "Negative" ? "🔴" : "⚪";
                sb.AppendLine($"{emoji} <b>{item.Symbol}</b>: {item.Title}");
                if (!string.IsNullOrEmpty(item.Summary))
                {
                    sb.AppendLine($"   <i>{item.Summary}</i>");
                }
                sb.AppendLine($"   📅 {item.PublishedAt:HH:mm} | {item.Source}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        #region Private Methods

        private async Task<List<StockNews>> GetFromGoogleNewsAsync(string symbol)
        {
            var news = new List<StockNews>();

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                var url = $"https://news.google.com/rss/search?q={symbol}+stock&hl=en-IN&gl=IN&ceid=IN:en";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    news = ParseGoogleRSS(content, symbol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google News failed for {Symbol}", symbol);
            }

            return news;
        }

        private async Task<List<StockNews>> GetFromYahooFinanceNewsAsync(string symbol)
        {
            var news = new List<StockNews>();

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                var url = $"https://query1.finance.yahoo.com/v1/finance/search?q={symbol}&newsCount=5";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<YahooNewsResponse>(content);

                    if (data?.news != null)
                    {
                        foreach (var item in data.news)
                        {
                            news.Add(new StockNews
                            {
                                Symbol = symbol,
                                Title = item.title,
                                Summary = item.summary,
                                Source = item.publisher,
                                PublishedAt = DateTimeOffset.FromUnixTimeSeconds(item.providerPublishTime).DateTime,
                                Url = item.link
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

        private List<StockNews> ParseGoogleRSS(string rssContent, string symbol)
        {
            var news = new List<StockNews>();

            try
            {
                // Simple RSS parsing (you might want to use System.Xml)
                var items = rssContent.Split("<item>");

                foreach (var item in items.Skip(1))
                {
                    var title = ExtractTag(item, "title");
                    var link = ExtractTag(item, "link");
                    var pubDate = ExtractTag(item, "pubDate");
                    var description = ExtractTag(item, "description");

                    if (!string.IsNullOrEmpty(title))
                    {
                        news.Add(new StockNews
                        {
                            Symbol = symbol,
                            Title = CleanHtml(title),
                            Summary = CleanHtml(description),
                            Source = "Google News",
                            PublishedAt = DateTime.TryParse(pubDate, out var date) ? date : DateTime.Now,
                            Url = link
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

        private List<StockNews> ParseRSS(string rssContent, string symbol)
        {
            // Similar to Google RSS parsing
            return new List<StockNews>();
        }

        private string ExtractTag(string content, string tag)
        {
            var startTag = $"<{tag}>";
            var endTag = $"</{tag}>";

            var start = content.IndexOf(startTag);
            if (start == -1) return "";

            start += startTag.Length;
            var end = content.IndexOf(endTag, start);
            if (end == -1) return "";

            return content.Substring(start, end - start);
        }

        private string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
        }

        private string AnalyzeSentiment(string text)
        {
            // Simple keyword-based sentiment analysis
            var positiveWords = new[] { "surge", "gain", "rise", "up", "positive", "bull", "growth", "profit", "high", "good" };
            var negativeWords = new[] { "fall", "drop", "down", "negative", "bear", "loss", "low", "poor", "decline", "crash" };

            text = text.ToLower();

            var positiveCount = positiveWords.Count(w => text.Contains(w));
            var negativeCount = negativeWords.Count(w => text.Contains(w));

            if (positiveCount > negativeCount) return "Positive";
            if (negativeCount > positiveCount) return "Negative";
            return "Neutral";
        }

        #endregion
    }    
}