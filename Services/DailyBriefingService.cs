using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;

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

        public DailyBriefingService(
            IStockService stockService,
            IStockListService stockListService,
            IAIService aiService,
            INewsService newsService,
            IPortfolioService portfolioService,
            ITelegramBotService telegramBot,
            ILogger<DailyBriefingService> logger)
        {
            _stockService = stockService;
            _stockListService = stockListService;
            _aiService = aiService;
            _newsService = newsService;
            _portfolioService = portfolioService;
            _telegramBot = telegramBot;
            _logger = logger;
        }

        public async Task<DailyBriefing> GenerateDailyBriefingAsync()
        {
            _logger.LogInformation("Generating daily briefing...");

            var briefing = new DailyBriefing
            {
                Date = DateTime.Now,
                Indices = await GetMarketIndicesAsync()
            };

            try
            {
                // Get all stocks
                var allStocks = await _stockService.GetIndianStockDataAsync();

                // Categorize by market cap
                var categorized = await CategorizeStocksByMarketCap(allStocks);

                // Get AI predictions for each category
                briefing.LargeCapPicks = await GetTopPredictions(categorized.LargeCap, 10);
                briefing.MidCapPicks = await GetTopPredictions(categorized.MidCap, 10);
                briefing.SmallCapPicks = await GetTopPredictions(categorized.SmallCap, 10);

                // Get market summary
                briefing.MarketSummary = await _aiService.GetMarketInsightAsync(allStocks);

                // Get top news
                briefing.TopNews = await _newsService.GetTopMarketNewsAsync(10);

                // Calculate sector performance
                briefing.SectorPerformance = await CalculateSectorPerformance(allStocks);

                // Determine top pick
                briefing.TopPick = DetermineTopPick(briefing.LargeCapPicks, briefing.MidCapPicks, briefing.SmallCapPicks);

                _logger.LogInformation("Daily briefing generated successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating daily briefing");
            }

            return briefing;
        }

        public async Task SendBriefingToUserAsync(long chatId)
        {
            var briefing = await GenerateDailyBriefingAsync();
            var message = FormatBriefingForTelegram(briefing);

            // Add portfolio-specific insights if user has portfolio
            var portfolio = await _portfolioService.GetUserPortfolioAsync(chatId);
            if (portfolio?.Holdings.Any() == true)
            {
                var portfolioInsights = await GetPortfolioInsights(chatId, briefing);
                message += "\n\n" + portfolioInsights;
            }

            await _telegramBot.SendMessageAsync(chatId, message);
        }

        public async Task SendBriefingToAllSubscribersAsync()
        {
            var portfolios = await _portfolioService.GetAllPortfoliosAsync();
            var briefing = await GenerateDailyBriefingAsync();
            var message = FormatBriefingForTelegram(briefing);

            foreach (var portfolio in portfolios)
            {
                try
                {
                    // Add personalized portfolio insights
                    var personalizedMessage = message;
                    if (portfolio.Holdings.Any())
                    {
                        var insights = await GetPortfolioInsights(portfolio.ChatId, briefing);
                        personalizedMessage += "\n\n" + insights;
                    }

                    await _telegramBot.SendMessageAsync(portfolio.ChatId, personalizedMessage);
                    await Task.Delay(100); // Rate limiting
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending briefing to user {ChatId}", portfolio.ChatId);
                }
            }
        }

        public string FormatBriefingForTelegram(DailyBriefing briefing)
        {
            var sb = new StringBuilder();

            // Header
            sb.AppendLine($"<b>📊 DAILY MARKET BRIEFING</b>");
            sb.AppendLine($"<b>{briefing.Date:dddd, MMMM d, yyyy}</b>\n");

            // Market Summary
            sb.AppendLine($"<b>📈 Market Summary</b>");
            sb.AppendLine($"{briefing.MarketSummary}\n");

            // Indices
            sb.AppendLine($"<b>📊 Indices</b>");
            sb.AppendLine($"Nifty 50: {briefing.Indices.Nifty50.Value:F2} " +
                         $"{GetChangeEmoji(briefing.Indices.Nifty50.ChangePercent)} " +
                         $"{briefing.Indices.Nifty50.ChangePercent:F2}%");
            sb.AppendLine($"Sensex: {briefing.Indices.Sensex.Value:F2} " +
                         $"{GetChangeEmoji(briefing.Indices.Sensex.ChangePercent)} " +
                         $"{briefing.Indices.Sensex.ChangePercent:F2}%");
            sb.AppendLine($"Bank Nifty: {briefing.Indices.BankNifty.Value:F2} " +
                         $"{GetChangeEmoji(briefing.Indices.BankNifty.ChangePercent)} " +
                         $"{briefing.Indices.BankNifty.ChangePercent:F2}%\n");

            // Top Pick
            if (!string.IsNullOrEmpty(briefing.TopPick))
            {
                sb.AppendLine($"🏆 <b>TOP PICK TODAY: {briefing.TopPick}</b>\n");
            }

            // Large Cap Picks
            sb.AppendLine($"<b>🏢 LARGE CAP PICKS</b>");
            foreach (var stock in briefing.LargeCapPicks)
            {
                sb.AppendLine($"• <b>{stock.Symbol}</b>: {stock.Recommendation} " +
                             $"(Confidence: {stock.Confidence})");
                if (stock.KeyFactors.Any())
                {
                    sb.AppendLine($"  <i>{stock.KeyFactors.First()}</i>");
                }
            }
            sb.AppendLine();

            // Mid Cap Picks
            sb.AppendLine($"<b>🏭 MID CAP PICKS</b>");
            foreach (var stock in briefing.MidCapPicks)
            {
                sb.AppendLine($"• <b>{stock.Symbol}</b>: {stock.Recommendation} " +
                             $"(Confidence: {stock.Confidence})");
                if (stock.KeyFactors.Any())
                {
                    sb.AppendLine($"  <i>{stock.KeyFactors.First()}</i>");
                }
            }
            sb.AppendLine();

            // Small Cap Picks
            sb.AppendLine($"<b>🏗️ SMALL CAP PICKS</b>");
            foreach (var stock in briefing.SmallCapPicks)
            {
                sb.AppendLine($"• <b>{stock.Symbol}</b>: {stock.Recommendation} " +
                             $"(Confidence: {stock.Confidence})");
                if (stock.KeyFactors.Any())
                {
                    sb.AppendLine($"  <i>{stock.KeyFactors.First()}</i>");
                }
            }
            sb.AppendLine();

            // Sector Performance
            if (briefing.SectorPerformance.Any())
            {
                sb.AppendLine($"<b>📌 SECTOR PERFORMANCE</b>");
                foreach (var sector in briefing.SectorPerformance.Take(5))
                {
                    var emoji = sector.Value.Contains("🟢") ? "🟢" : sector.Value.Contains("🔴") ? "🔴" : "⚪";
                    sb.AppendLine($"{emoji} {sector.Key}: {sector.Value}");
                }
                sb.AppendLine();
            }

            // Top News
            if (briefing.TopNews.Any())
            {
                sb.AppendLine($"<b>📰 TOP NEWS</b>");
                foreach (var news in briefing.TopNews.Take(3))
                {
                    var sentimentEmoji = news.Sentiment == "Positive" ? "🟢" :
                                        news.Sentiment == "Negative" ? "🔴" : "⚪";
                    sb.AppendLine($"{sentimentEmoji} <b>{news.Symbol}</b>: {news.Title}");
                }
                sb.AppendLine();
            }

            // Footer
            sb.AppendLine($"<i>Daily briefing powered by AI • {briefing.Date:dd MMM yyyy}</i>");
            sb.AppendLine($"<i>Use /help for all commands</i>");

            return sb.ToString();
        }

        #region Private Methods

        private async Task<MarketIndices> GetMarketIndicesAsync()
        {
            var indices = new MarketIndices();

            try
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                // Get Nifty 50
                var niftyResponse = await client.GetAsync("https://query1.finance.yahoo.com/v8/finance/chart/^NSEI");
                // Parse response (simplified - implement actual parsing)

                // For demo, return sample data
                indices.Nifty50 = new IndexData { Name = "Nifty 50", Value = 22456.80m, Change = 123.45m, ChangePercent = 0.55m };
                indices.Sensex = new IndexData { Name = "Sensex", Value = 73896.55m, Change = 345.67m, ChangePercent = 0.47m };
                indices.BankNifty = new IndexData { Name = "Bank Nifty", Value = 48567.90m, Change = 234.56m, ChangePercent = 0.48m };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching indices");
            }

            return indices;
        }

        private async Task<(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap)>
            CategorizeStocksByMarketCap(List<StockData> stocks)
        {
            var largeCap = new List<StockData>();
            var midCap = new List<StockData>();
            var smallCap = new List<StockData>();

            foreach (var stock in stocks)
            {
                if (stock.MarketCap >= 20000) // 20,000 Cr+
                {
                    largeCap.Add(stock);
                }
                else if (stock.MarketCap >= 5000) // 5,000 - 20,000 Cr
                {
                    midCap.Add(stock);
                }
                else
                {
                    smallCap.Add(stock);
                }
            }

            return (largeCap, midCap, smallCap);
        }

        private async Task<List<StockPrediction>> GetTopPredictions(List<StockData> stocks, int count)
        {
            if (!stocks.Any()) return new List<StockPrediction>();

            var predictions = await _aiService.GeneratePredictionsAsync(stocks);

            return predictions.Predictions
                .Where(p => p.Recommendation == "Buy" || p.Recommendation == "Strong Buy")
                .OrderByDescending(p => p.Confidence == "High" ? 3 : p.Confidence == "Medium" ? 2 : 1)
                .ThenByDescending(p => p.CurrentPrice)
                .Take(count)
                .ToList();
        }

        private async Task<Dictionary<string, string>> CalculateSectorPerformance(List<StockData> stocks)
        {
            var sectorPerformance = new Dictionary<string, string>();

            var sectors = stocks.GroupBy(s => s.Sector ?? "Other");

            foreach (var sector in sectors)
            {
                var avgChange = sector.Average(s => s.ChangePercent);
                var emoji = avgChange > 1 ? "🟢" : avgChange < -1 ? "🔴" : "⚪";
                sectorPerformance[sector.Key] = $"{emoji} {avgChange:F2}%";
            }

            return sectorPerformance;
        }

        private string DetermineTopPick(List<StockPrediction> largeCap, List<StockPrediction> midCap, List<StockPrediction> smallCap)
        {
            var allPicks = largeCap.Concat(midCap).Concat(smallCap)
                .Where(p => p.Recommendation == "Buy" && p.Confidence == "High")
                .ToList();

            return allPicks.FirstOrDefault()?.Symbol ??
                   largeCap.FirstOrDefault()?.Symbol ??
                   "RELIANCE";
        }

        private string GetChangeEmoji(decimal change)
        {
            return change > 0 ? "🟢" : change < 0 ? "🔴" : "⚪";
        }

        private async Task<string> GetPortfolioInsights(long chatId, DailyBriefing briefing)
        {
            var portfolio = await _portfolioService.GetUserPortfolioAsync(chatId);
            if (portfolio?.Holdings == null || !portfolio.Holdings.Any())
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("<b>📊 YOUR PORTFOLIO INSIGHTS</b>");

            foreach (var holding in portfolio.Holdings.Take(5))
            {
                // Find if this stock is in today's picks
                var isPick = briefing.LargeCapPicks.Any(p => p.Symbol == holding.Symbol) ||
                            briefing.MidCapPicks.Any(p => p.Symbol == holding.Symbol) ||
                            briefing.SmallCapPicks.Any(p => p.Symbol == holding.Symbol);

                if (isPick)
                {
                    sb.AppendLine($"✅ <b>{holding.Symbol}</b> is in today's picks!");
                }
            }

            return sb.ToString();
        }

        #endregion
    }
}