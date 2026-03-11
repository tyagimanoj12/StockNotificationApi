using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;

namespace StockNotificationApi.Services
{
    public class DailyBriefingService : IDailyBriefingService
    {
        private readonly IStockService _stockService;
        private readonly IStockListService _stockListService;
        private readonly IAIService _aiService;
        private readonly INewsService _newsService;
        private readonly IPortfolioService _portfolioService;
        private readonly ILogger<DailyBriefingService> _logger;
        private readonly ITelegramBotService _telegramBot;
        private readonly IHttpClientFactory _httpClientFactory;

        // Constants
        private const int LARGE_CAP_THRESHOLD = 20000;
        private const int MID_CAP_THRESHOLD = 5000;
        private const int MAX_PICKS_PER_CATEGORY = 10;
        private const int TOP_NEWS_COUNT = 10;
        private const int PORTFOLIO_DISPLAY_LIMIT = 5;
        private const int RATE_LIMIT_DELAY_MS = 100;

        // Yahoo Finance symbols for Indian indices
        private const string YAHOO_NIFTY_SYMBOL = "^NSEI";
        private const string YAHOO_SENSEX_SYMBOL = "^BSESN";
        private const string YAHOO_BANKNIFTY_SYMBOL = "^NSEBANK";

        public DailyBriefingService(
            IStockService stockService,
            IStockListService stockListService,
            IAIService aiService,
            INewsService newsService,
            IPortfolioService portfolioService,
            ITelegramBotService telegramBot,
            IHttpClientFactory httpClientFactory,
            ILogger<DailyBriefingService> logger)
        {
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _stockListService = stockListService ?? throw new ArgumentNullException(nameof(stockListService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _newsService = newsService ?? throw new ArgumentNullException(nameof(newsService));
            _portfolioService = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
            _telegramBot = telegramBot ?? throw new ArgumentNullException(nameof(telegramBot));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<DailyBriefing> GenerateDailyBriefingAsync()
        {
            _logger.LogInformation("Generating daily briefing...");

            var briefing = new DailyBriefing
            {
                Date = DateTime.Now,
                Indices = await GetMarketIndicesAsync(),
                LargeCapPicks = new List<StockPrediction>(),
                MidCapPicks = new List<StockPrediction>(),
                SmallCapPicks = new List<StockPrediction>(),
                TopNews = new List<StockNews>(),
                SectorPerformance = new Dictionary<string, string>()
            };

            try
            {
                // Get all stocks
                var allStocks = await _stockService.GetIndianStockDataAsync();
                if (allStocks == null || !allStocks.Any())
                {
                    _logger.LogWarning("No stock data available for briefing");
                    return briefing;
                }

                // Categorize by market cap
                var categorized = await CategorizeStocksByMarketCap(allStocks);

                // Get AI predictions for each category
                briefing.LargeCapPicks = await GetTopPredictions(categorized.LargeCap, MAX_PICKS_PER_CATEGORY);
                briefing.MidCapPicks = await GetTopPredictions(categorized.MidCap, MAX_PICKS_PER_CATEGORY);
                briefing.SmallCapPicks = await GetTopPredictions(categorized.SmallCap, MAX_PICKS_PER_CATEGORY);

                // Get market summary
                briefing.MarketSummary = await _aiService.GetMarketInsightAsync(allStocks) ?? "Market summary unavailable";

                // Get top news
                briefing.TopNews = await _newsService.GetTopMarketNewsAsync(TOP_NEWS_COUNT) ?? new List<StockNews>();

                // Calculate sector performance
                briefing.SectorPerformance = await CalculateSectorPerformance(allStocks);

                // Determine top pick
                briefing.TopPick = DetermineTopPick(briefing.LargeCapPicks, briefing.MidCapPicks, briefing.SmallCapPicks);

                _logger.LogInformation("Daily briefing generated successfully with {LargeCount} large, {MidCount} mid, {SmallCount} small cap picks",
                    briefing.LargeCapPicks.Count, briefing.MidCapPicks.Count, briefing.SmallCapPicks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating daily briefing");
            }

            return briefing;
        }

        public async Task SendBriefingToUserAsync(long chatId)
        {
            if (chatId <= 0)
            {
                _logger.LogWarning("Invalid chat ID: {ChatId}", chatId);
                return;
            }

            try
            {
                var briefing = await GenerateDailyBriefingAsync();
                var message = FormatBriefingForTelegram(briefing);

                // Add portfolio-specific insights if user has portfolio
                var portfolio = await _portfolioService.GetUserPortfolioAsync(chatId);
                if (portfolio?.Holdings?.Any() == true)
                {
                    var portfolioInsights = await GetPortfolioInsights(chatId, briefing);
                    if (!string.IsNullOrEmpty(portfolioInsights))
                    {
                        message += "\n\n" + portfolioInsights;
                    }
                }

                await _telegramBot.SendMessageAsync(chatId, message);
                _logger.LogInformation("Briefing sent to user {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending briefing to user {ChatId}", chatId);
            }
        }

        public async Task SendBriefingToAllSubscribersAsync()
        {
            try
            {
                var portfolios = await _portfolioService.GetAllPortfoliosAsync();
                if (portfolios == null || !portfolios.Any())
                {
                    _logger.LogInformation("No portfolio subscribers found");
                    return;
                }

                var briefing = await GenerateDailyBriefingAsync();
                var baseMessage = FormatBriefingForTelegram(briefing);

                int successCount = 0;
                foreach (var portfolio in portfolios)
                {
                    try
                    {
                        if (portfolio?.ChatId <= 0) continue;

                        // Add personalized portfolio insights
                        var personalizedMessage = baseMessage;
                        if (portfolio.Holdings?.Any() == true)
                        {
                            var insights = await GetPortfolioInsights(portfolio.ChatId, briefing);
                            if (!string.IsNullOrEmpty(insights))
                            {
                                personalizedMessage += "\n\n" + insights;
                            }
                        }

                        await _telegramBot.SendMessageAsync(portfolio.ChatId, personalizedMessage);
                        successCount++;
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending briefing to user {ChatId}", portfolio?.ChatId);
                    }
                }

                _logger.LogInformation("Briefing sent to {SuccessCount}/{TotalCount} subscribers",
                    successCount, portfolios.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending briefing to all subscribers");
            }
        }

        public string FormatBriefingForTelegram(DailyBriefing briefing)
        {
            if (briefing == null)
            {
                return "<b>📊 Briefing unavailable at this moment.</b>";
            }

            var sb = new StringBuilder();

            // Header
            sb.AppendLine($"<b>📊 DAILY MARKET BRIEFING</b>");
            sb.AppendLine($"<b>{briefing.Date:dddd, MMMM d, yyyy}</b>\n");

            // Market Summary
            sb.AppendLine($"<b>📈 Market Summary</b>");
            sb.AppendLine($"{briefing.MarketSummary ?? "No summary available"}\n");

            // Indices
            if (briefing.Indices != null)
            {
                sb.AppendLine($"<b>📊 Indices</b>");
                AppendIndexLine(sb, "Nifty 50", briefing.Indices.Nifty50);
                AppendIndexLine(sb, "Sensex", briefing.Indices.Sensex);
                AppendIndexLine(sb, "Bank Nifty", briefing.Indices.BankNifty);
                sb.AppendLine();
            }

            // Top Pick
            if (!string.IsNullOrEmpty(briefing.TopPick))
            {
                sb.AppendLine($"🏆 <b>TOP PICK TODAY: {briefing.TopPick}</b>\n");
            }

            // Large Cap Picks
            AppendPicksSection(sb, "🏢 LARGE CAP PICKS", briefing.LargeCapPicks);

            // Mid Cap Picks
            AppendPicksSection(sb, "🏭 MID CAP PICKS", briefing.MidCapPicks);

            // Small Cap Picks
            AppendPicksSection(sb, "🏗️ SMALL CAP PICKS", briefing.SmallCapPicks);

            // Sector Performance
            if (briefing.SectorPerformance?.Any() == true)
            {
                sb.AppendLine($"<b>📌 SECTOR PERFORMANCE</b>");
                foreach (var sector in briefing.SectorPerformance.Take(5))
                {
                    sb.AppendLine($"{sector.Value}");
                }
                sb.AppendLine();
            }

            // Top News
            if (briefing.TopNews?.Any() == true)
            {
                sb.AppendLine($"<b>📰 TOP NEWS</b>");
                foreach (var news in briefing.TopNews.Take(3))
                {
                    if (news != null)
                    {
                        var sentimentEmoji = GetSentimentEmoji(news.Sentiment);
                        sb.AppendLine($"{sentimentEmoji} <b>{news.Symbol ?? "Market"}</b>: {news.Title ?? "No title"}");
                    }
                }
                sb.AppendLine();
            }

            // Footer
            sb.AppendLine($"<i>Daily briefing powered by AI • {briefing.Date:dd MMM yyyy}</i>");
            sb.AppendLine($"<i>Use /help for all commands</i>");

            return sb.ToString();
        }

        #region Private Methods

        private void AppendIndexLine(StringBuilder sb, string name, IndexData index)
        {
            if (index != null)
            {
                var emoji = index.ChangePercent >= 0 ? "🟢" : "🔴";
                sb.AppendLine($"{name}: {index.Value:N2} {emoji} {index.ChangePercent:+#.##;-#.##;0}%");
            }
        }

        private void AppendPicksSection(StringBuilder sb, string title, List<StockPrediction> picks)
        {
            if (picks?.Any() == true)
            {
                sb.AppendLine($"<b>{title}</b>");
                foreach (var stock in picks.Take(3)) // Show top 3 picks per category
                {
                    var confidenceEmoji = stock.Confidence?.ToLower() switch
                    {
                        "high" => "🔴",
                        "medium" => "🟡",
                        _ => "⚪"
                    };
                    sb.AppendLine($"• <b>{stock.Symbol}</b>: {stock.Recommendation} {confidenceEmoji}");
                    if (stock.KeyFactors?.Any() == true)
                    {
                        sb.AppendLine($"  <i>{stock.KeyFactors.First()}</i>");
                    }
                }
                sb.AppendLine();
            }
        }

        private async Task<MarketIndices> GetMarketIndicesAsync()
        {
            var indices = new MarketIndices
            {
                Nifty50 = new IndexData { Name = "Nifty 50" },
                Sensex = new IndexData { Name = "Sensex" },
                BankNifty = new IndexData { Name = "Bank Nifty" }
            };

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.Timeout = TimeSpan.FromSeconds(30);

                // Fetch all indices in parallel
                var tasks = new[]
                {
                    GetIndexDataAsync(client, YAHOO_NIFTY_SYMBOL),
                    GetIndexDataAsync(client, YAHOO_SENSEX_SYMBOL),
                    GetIndexDataAsync(client, YAHOO_BANKNIFTY_SYMBOL)
                };

                var results = await Task.WhenAll(tasks);

                if (results[0] != null) indices.Nifty50 = results[0];
                if (results[1] != null) indices.Sensex = results[1];
                if (results[2] != null) indices.BankNifty = results[2];

                _logger.LogInformation("Indices fetched successfully - Nifty: {Nifty}, Sensex: {Sensex}, BankNifty: {BankNifty}",
                    indices.Nifty50.Value, indices.Sensex.Value, indices.BankNifty.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching indices, using fallback data");
                return GetFallbackIndices();
            }

            return indices;
        }

        private async Task<IndexData?> GetIndexDataAsync(HttpClient client, string symbol)
        {
            try
            {
                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?interval=1d";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch {Symbol}: {StatusCode}", symbol, response.StatusCode);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                // Navigate to the quote data
                if (root.TryGetProperty("chart", out var chart) &&
                    chart.TryGetProperty("result", out var result) &&
                    result.ValueKind == JsonValueKind.Array &&
                    result.GetArrayLength() > 0)
                {
                    var firstResult = result[0];

                    if (firstResult.TryGetProperty("meta", out var meta))
                    {
                        var indexData = new IndexData
                        {
                            Name = GetIndexName(symbol),
                            Value = meta.TryGetProperty("regularMarketPrice", out var price) ?
                                    price.GetDecimal() : 0,
                            PreviousClose = meta.TryGetProperty("previousClose", out var prevClose) ?
                                           prevClose.GetDecimal() : 0
                        };

                        // Calculate change and change percent
                        indexData.Change = indexData.Value - indexData.PreviousClose;
                        indexData.ChangePercent = indexData.PreviousClose > 0 ?
                            (indexData.Change / indexData.PreviousClose) * 100 : 0;

                        return indexData;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing index data for {Symbol}", symbol);
                return null;
            }
        }

        private string GetIndexName(string symbol)
        {
            return symbol switch
            {
                "^NSEI" => "Nifty 50",
                "^BSESN" => "Sensex",
                "^NSEBANK" => "Bank Nifty",
                _ => symbol
            };
        }

        private MarketIndices GetFallbackIndices()
        {
            _logger.LogWarning("Using fallback index data");

            return new MarketIndices
            {
                Nifty50 = new IndexData
                {
                    Name = "Nifty 50",
                    Value = 22456.80m,
                    PreviousClose = 22333.35m,
                    Change = 123.45m,
                    ChangePercent = 0.55m
                },
                Sensex = new IndexData
                {
                    Name = "Sensex",
                    Value = 73896.55m,
                    PreviousClose = 73550.88m,
                    Change = 345.67m,
                    ChangePercent = 0.47m
                },
                BankNifty = new IndexData
                {
                    Name = "Bank Nifty",
                    Value = 48567.90m,
                    PreviousClose = 48333.34m,
                    Change = 234.56m,
                    ChangePercent = 0.48m
                }
            };
        }

        private async Task<(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap)>
            CategorizeStocksByMarketCap(List<StockData> stocks)
        {
            if (stocks == null) return (new List<StockData>(), new List<StockData>(), new List<StockData>());

            var largeCap = new List<StockData>();
            var midCap = new List<StockData>();
            var smallCap = new List<StockData>();

            foreach (var stock in stocks)
            {
                if (stock == null) continue;

                if (stock.MarketCap >= LARGE_CAP_THRESHOLD)
                {
                    largeCap.Add(stock);
                }
                else if (stock.MarketCap >= MID_CAP_THRESHOLD)
                {
                    midCap.Add(stock);
                }
                else if (stock.MarketCap > 0)
                {
                    smallCap.Add(stock);
                }
            }

            return (largeCap, midCap, smallCap);
        }

        private async Task<List<StockPrediction>> GetTopPredictions(List<StockData> stocks, int count)
        {
            if (stocks == null || !stocks.Any()) return new List<StockPrediction>();

            try
            {
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                if (predictions?.Predictions == null) return new List<StockPrediction>();

                return predictions.Predictions
                    .Where(p => p != null && (p.Recommendation?.Equals("Buy", StringComparison.OrdinalIgnoreCase) == true))
                    .OrderByDescending(p => GetConfidenceScore(p.Confidence))
                    .ThenByDescending(p => p.CurrentPrice)
                    .Take(count)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting top predictions");
                return new List<StockPrediction>();
            }
        }

        private int GetConfidenceScore(string confidence)
        {
            return confidence?.ToLower() switch
            {
                "high" => 3,
                "medium" => 2,
                "low" => 1,
                _ => 0
            };
        }

        private async Task<Dictionary<string, string>> CalculateSectorPerformance(List<StockData> stocks)
        {
            var sectorPerformance = new Dictionary<string, string>();

            if (stocks == null || !stocks.Any()) return sectorPerformance;

            try
            {
                var sectors = stocks.Where(s => s != null)
                                   .GroupBy(s => s.Sector ?? "Other");

                foreach (var sector in sectors)
                {
                    var avgChange = sector.Average(s => s.ChangePercent);
                    var emoji = GetPerformanceEmoji(avgChange);
                    sectorPerformance[sector.Key] = $"{emoji} {sector.Key}: {avgChange:F2}%";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating sector performance");
            }

            return sectorPerformance;
        }

        private string GetPerformanceEmoji(decimal change)
        {
            return change > 1 ? "🟢" : change < -1 ? "🔴" : "⚪";
        }

        private string DetermineTopPick(List<StockPrediction> largeCap, List<StockPrediction> midCap, List<StockPrediction> smallCap)
        {
            var allPicks = new List<StockPrediction>();

            if (largeCap?.Any() == true) allPicks.AddRange(largeCap);
            if (midCap?.Any() == true) allPicks.AddRange(midCap);
            if (smallCap?.Any() == true) allPicks.AddRange(smallCap);

            var topPick = allPicks
                .Where(p => p != null && p.Recommendation?.Equals("Buy", StringComparison.OrdinalIgnoreCase) == true &&
                           p.Confidence?.Equals("High", StringComparison.OrdinalIgnoreCase) == true)
                .OrderByDescending(p => p.CurrentPrice)
                .FirstOrDefault();

            return topPick?.Symbol ?? largeCap?.FirstOrDefault()?.Symbol ?? "RELIANCE";
        }

        private string GetChangeEmoji(decimal change)
        {
            return change > 0 ? "🟢" : change < 0 ? "🔴" : "⚪";
        }

        private string GetSentimentEmoji(string sentiment)
        {
            return sentiment?.ToLower() switch
            {
                "positive" => "🟢",
                "negative" => "🔴",
                _ => "⚪"
            };
        }

        private async Task<string> GetPortfolioInsights(long chatId, DailyBriefing briefing)
        {
            try
            {
                var portfolio = await _portfolioService.GetUserPortfolioAsync(chatId);
                if (portfolio?.Holdings == null || !portfolio.Holdings.Any())
                    return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("<b>📊 YOUR PORTFOLIO INSIGHTS</b>");

                var picksInPortfolio = new List<string>();

                foreach (var holding in portfolio.Holdings.Take(PORTFOLIO_DISPLAY_LIMIT))
                {
                    if (holding == null) continue;

                    var isPick = briefing.LargeCapPicks?.Any(p => p?.Symbol == holding.Symbol) == true ||
                                briefing.MidCapPicks?.Any(p => p?.Symbol == holding.Symbol) == true ||
                                briefing.SmallCapPicks?.Any(p => p?.Symbol == holding.Symbol) == true;

                    if (isPick)
                    {
                        picksInPortfolio.Add(holding.Symbol);
                    }
                }

                if (picksInPortfolio.Any())
                {
                    foreach (var symbol in picksInPortfolio)
                    {
                        sb.AppendLine($"✅ <b>{symbol}</b> is in today's picks!");
                    }
                }
                else
                {
                    sb.AppendLine("No holdings in today's picks.");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting portfolio insights for chat {ChatId}", chatId);
                return string.Empty;
            }
        }

        #endregion
    }
}