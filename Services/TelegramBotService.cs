using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using StockNotificationApi.Models;
using StockNotificationApi.Interfaces;
using System.Text;

namespace StockNotificationApi.Services
{
    public class TelegramBotService : BackgroundService, ITelegramBotService
    {
        private readonly ILogger<TelegramBotService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;
        private TelegramBotClient? _botClient;
        private readonly List<long> _subscribedChats = new();

        public TelegramBotService(
            ILogger<TelegramBotService> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _scopeFactory = scopeFactory;
        }

        public async Task SendMessageAsync(long chatId, string message, ParseMode parseMode = ParseMode.Html)
        {
            try
            {
                if (_botClient == null) return;
                await _botClient.SendMessage(chatId, message, parseMode: parseMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message");
            }
        }

        public async Task BroadcastToAllAsync(string message)
        {
            foreach (var chatId in _subscribedChats)
            {
                await SendMessageAsync(chatId, message);
                await Task.Delay(100);
            }
        }

        public async Task SendDailyReportToAllAsync(DailyPredictionReport report)
        {
            var message = FormatDailyReport(report);
            await BroadcastToAllAsync(message);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var token = _configuration["TelegramSettings:BotToken"];
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Telegram bot token not configured");
                return;
            }

            _botClient = new TelegramBotClient(token);
            _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, cancellationToken: stoppingToken);

            var me = await _botClient.GetMe();
            _logger.LogInformation("Bot started: @{Username}", me.Username);

            // Daily report scheduler
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.Now.ToString("HH:mm") == "09:00")
                {
                    await SendScheduledDailyReport();
                    await Task.Delay(60000, stoppingToken);
                }
                await Task.Delay(30000, stoppingToken);
            }

            // Daily briefing scheduler (8:30 AM)
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                if (now.Hour == 8 && now.Minute == 30) // 8:30 AM
                {
                    using var scope = _scopeFactory.CreateScope();
                    var briefingService = scope.ServiceProvider.GetRequiredService<IDailyBriefingService>();
                    await briefingService.SendBriefingToAllSubscribersAsync();
                    await Task.Delay(60000, stoppingToken); // Avoid multiple sends
                }
                await Task.Delay(30000, stoppingToken);
            }
        }

        //private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken token)
        //{
        //    if (update.Message?.Text == null) return;

        //    var chatId = update.Message.Chat.Id;
        //    var text = update.Message.Text;

        //    switch (text.ToLower())
        //    {
        //        case "/start":
        //            await SendWelcomeMessage(chatId, update.Message.Chat);
        //            break;
        //        case "/help":
        //            await SendHelpMessage(chatId);
        //            break;
        //        case "/stocks":
        //            await SendTopStocks(chatId);
        //            break;
        //        case "/subscribe":
        //            await SubscribeUser(chatId);
        //            break;
        //        case "/unsubscribe":
        //            await UnsubscribeUser(chatId);
        //            break;
        //        default:
        //            if (text.StartsWith("/price"))
        //            {
        //                var symbol = text.Replace("/price", "").Trim();
        //                await SendStockPrice(chatId, symbol);
        //            }
        //            break;
        //    }
        //}
        //private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken token)
        //{
        //    var chatId = update.Message.Chat.Id;

        //    try
        //    {
        //        if (update.Message?.Text == null) return;

        //        var text = update.Message.Text.Trim();
        //        var lowerText = text.ToLower();

        //        _logger.LogInformation("Received message: {Text} from {ChatId}", text, chatId);

        //        switch (lowerText)
        //        {
        //            case "/start":
        //                await SendWelcomeMessage(chatId, update.Message.Chat);
        //                break;

        //            case "/help":
        //                await SendHelpMessage(chatId);
        //                break;

        //            case "/stocks":
        //                await SendTopStocks(chatId);
        //                break;

        //            case "/subscribe":
        //                await SubscribeUser(chatId);
        //                break;

        //            case "/unsubscribe":
        //                await UnsubscribeUser(chatId);
        //                break;

        //            default:
        //                // Check if it's a /price command
        //                if (lowerText.StartsWith("/price"))
        //                {
        //                    // Check if there's anything after /price
        //                    if (text.Length <= 6) // Just "/price" with no symbol
        //                    {
        //                        await SendMessageAsync(chatId, "❌ Please provide a symbol. Example: /price RELIANCE");
        //                    }
        //                    else
        //                    {
        //                        // Extract symbol (remove "/price " and clean up)
        //                        var symbol = text.Substring(6).Trim();
        //                        if (!string.IsNullOrWhiteSpace(symbol))
        //                        {
        //                            await SendStockPrice(chatId, symbol.ToUpper());
        //                        }
        //                        else
        //                        {
        //                            await SendMessageAsync(chatId, "❌ Please provide a symbol. Example: /price RELIANCE");
        //                        }
        //                    }
        //                }
        //                else
        //                {
        //                    await SendMessageAsync(chatId, "❌ Unknown command. Type /help for available commands.");
        //                }
        //                break;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error handling update");
        //        await SendMessageAsync(chatId, "❌ An error occurred. Please try again.");
        //    }
        //}
        private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken token)
        {
            var chatId = update.Message.Chat.Id;

            try
            {
                if (update.Message?.Text == null) return;

                var text = update.Message.Text.Trim();
                var lowerText = text.ToLower();

                _logger.LogInformation("Received message: {Text} from {ChatId}", text, chatId);

                switch (lowerText)
                {
                    case "/start":
                        await SendWelcomeMessage(chatId, update.Message.Chat);
                        break;

                    case "/help":
                        await SendHelpMessage(chatId);
                        break;

                    case "/stocks":
                        await SendTopStocks(chatId);
                        break;

                    case "/topgainers":
                        await SendTopGainers(chatId);
                        break;

                    case "/toplosers":
                        await SendTopLosers(chatId);
                        break;

                    case "/subscribe":
                        await SubscribeUser(chatId);
                        break;

                    case "/unsubscribe":
                        await UnsubscribeUser(chatId);
                        break;

                    case "/briefing":
                        await SendDailyBriefing(chatId);
                        break;

                    case "/subscribe_briefing":
                        await SubscribeToBriefing(chatId);
                        break;

                    case "/unsubscribe_briefing":
                        await UnsubscribeFromBriefing(chatId);
                        break;
                    // Add to HandleUpdateAsync
                    case "/analysis":
                        await SendEnhancedAnalysis(chatId);
                        break;

                    case "/largecap":
                        await SendCategoryPicks(chatId, "large");
                        break;

                    case "/midcap":
                        await SendCategoryPicks(chatId, "mid");
                        break;

                    case "/smallcap":
                        await SendCategoryPicks(chatId, "small");
                        break;

                    case "/technical":
                        await SendTechnicalIndicators(chatId);
                        break;

                    default:
                        // Check if it's a /price command
                        if (lowerText.StartsWith("/price"))
                        {
                            if (text.Length <= 6)
                            {
                                await SendMessageAsync(chatId, "❌ Please provide a symbol. Example: /price RELIANCE");
                            }
                            else
                            {
                                var symbol = text.Substring(6).Trim();
                                if (!string.IsNullOrWhiteSpace(symbol))
                                {
                                    await SendStockPrice(chatId, symbol.ToUpper());
                                }
                                else
                                {
                                    await SendMessageAsync(chatId, "❌ Please provide a symbol. Example: /price RELIANCE");
                                }
                            }
                        }
                        else
                        {
                            await SendMessageAsync(chatId, "❌ Unknown command. Type /help for available commands.");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update");
                await SendMessageAsync(chatId, "❌ An error occurred. Please try again.");
            }
        }
        private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
        {
            _logger.LogError(exception, "Telegram bot error");
            return Task.CompletedTask;
        }

        private async Task SendWelcomeMessage(long chatId, Chat chat)
        {
            var message = $@"<b>🤖 Welcome to Indian Stock Predictor Bot!</b>

Hello {chat.FirstName}! I can help you with:
• 📈 Real-time stock prices
• 📊 Daily market predictions
• 🏆 Top gainers & losers
• 📉 Market trends

<b>Commands:</b>
/help - Show all commands
/stocks - View top 10 stocks
/topgainers - Today's top gainers
/toplosers - Today's top losers
/price RELIANCE - Get stock price
/subscribe - Get daily updates

<b>Get started by trying /stocks or /topgainers!</b>";

            await SendMessageAsync(chatId, message);
        }

        //        private async Task SendWelcomeMessage(long chatId, Chat chat)
        //        {
        //            var message = $@"<b>🤖 Welcome to Indian Stock Predictor Bot!</b>

        //Hello {chat.FirstName}! I can help you with:
        //• 📈 Real-time stock prices
        //• 📊 Daily market predictions
        //• 🏆 Top gainers/losers

        //<b>Commands:</b>
        ///help - Show all commands
        ///subscribe - Get daily updates
        ///stocks - View top 10 stocks
        ///price RELIANCE - Get stock price";

        //            await SendMessageAsync(chatId, message);
        //        }

        //        private async Task SendHelpMessage(long chatId)
        //        {
        //            var message = @"<b>📚 Available Commands</b>

        //<b>Basic Commands:</b>
        ///start - Welcome message
        ///help - Show this help

        //<b>Stock Commands:</b>
        ///stocks - View top 10 active stocks
        ///topgainers - View top 5 gainers today
        ///toplosers - View top 5 losers today
        ///price SYMBOL - Get price (e.g., /price RELIANCE)

        //<b>Subscription Commands:</b>
        ///subscribe - Get daily updates
        ///unsubscribe - Stop updates

        //<b>Examples:</b>
        ///price TCS
        ///price HDFCBANK
        ///topgainers
        ///toplosers";

        //            await SendMessageAsync(chatId, message);
        //        }

        //        private async Task SendHelpMessage(long chatId)
        //        {
        //            var message = @"<b>📚 Available Commands</b>

        ///start - Welcome message
        ///help - Show this help
        ///subscribe - Get daily updates
        ///unsubscribe - Stop updates
        ///stocks - View top 10 stocks
        ///price SYMBOL - Get price (e.g., /price RELIANCE)";

        //            await SendMessageAsync(chatId, message);
        //        }

        private async Task SendHelpMessage(long chatId)
        {
            var message = @"<b>📚 Available Commands</b>

<b>Stock Commands:</b>
/stocks - View top 10 active stocks
/topgainers - View top 5 gainers today
/toplosers - View top 5 losers today
/price SYMBOL - Get price (e.g., /price RELIANCE)

<b>📊 Daily Briefing:</b>
/briefing - Get today's AI-powered briefing
/subscribe_briefing - Auto-receive daily briefing
/unsubscribe_briefing - Stop daily briefing

<b>📈 Portfolio Commands:</b>
/portfolio - View your holdings
/performance - View portfolio P&L
/add SYMBOL QTY PRICE - Add to portfolio
/remove SYMBOL - Remove from portfolio

<b>Subscription:</b>
/subscribe - Get daily updates
/unsubscribe - Stop updates";

            await SendMessageAsync(chatId, message);
        }

        private async Task SendTopGainers(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                var stocks = await stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    await SendMessageAsync(chatId, "❌ Unable to fetch stock data at the moment.");
                    return;
                }

                // Get top 5 gainers (highest positive change percent)
                var gainers = stocks
                    .Where(s => s.ChangePercent > 0)
                    .OrderByDescending(s => s.ChangePercent)
                    .Take(5)
                    .ToList();

                if (!gainers.Any())
                {
                    await SendMessageAsync(chatId, "📊 No gainers at the moment.");
                    return;
                }

                var message = new StringBuilder();
                message.AppendLine("<b>📈 Top 5 Gainers Today</b>\n");

                foreach (var stock in gainers)
                {
                    message.AppendLine($"<b>{stock.Symbol}</b>: ₹{stock.Price:F2} 📈 +{stock.ChangePercent:F2}%");
                    message.AppendLine($"   Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}");
                    message.AppendLine();
                }

                await SendMessageAsync(chatId, message.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top gainers");
                await SendMessageAsync(chatId, "❌ Error fetching gainers data.");
            }
        }

        private async Task SendTopLosers(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                var stocks = await stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    await SendMessageAsync(chatId, "❌ Unable to fetch stock data at the moment.");
                    return;
                }

                // Get top 5 losers (highest negative change percent)
                var losers = stocks
                    .Where(s => s.ChangePercent < 0)
                    .OrderBy(s => s.ChangePercent)  // Most negative first
                    .Take(5)
                    .ToList();

                if (!losers.Any())
                {
                    await SendMessageAsync(chatId, "📊 No losers at the moment.");
                    return;
                }

                var message = new StringBuilder();
                message.AppendLine("<b>📉 Top 5 Losers Today</b>\n");

                foreach (var stock in losers)
                {
                    message.AppendLine($"<b>{stock.Symbol}</b>: ₹{stock.Price:F2} 📉 {stock.ChangePercent:F2}%");
                    message.AppendLine($"   Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}");
                    message.AppendLine();
                }

                await SendMessageAsync(chatId, message.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top losers");
                await SendMessageAsync(chatId, "❌ Error fetching losers data.");
            }
        }

        private async Task SendTopStocks(long chatId)
        {
            using var scope = _scopeFactory.CreateScope();
            var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

            var stocks = await stockService.GetIndianStockDataAsync();
            var topStocks = stocks.OrderByDescending(s => Math.Abs(s.ChangePercent)).Take(10);

            var message = new StringBuilder();
            message.AppendLine("<b>🏆 Top 10 Active Stocks</b>\n");

            foreach (var stock in topStocks)
            {
                var arrow = stock.ChangePercent > 0 ? "📈" : "📉";
                message.AppendLine($"<b>{stock.Symbol}</b>: ₹{stock.Price:F2} {arrow} {Math.Abs(stock.ChangePercent):F2}%");
            }

            await SendMessageAsync(chatId, message.ToString());
        }

        private async Task SendStockPrice(long chatId, string symbol)
        {
            using var scope = _scopeFactory.CreateScope();
            var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

            var stock = await stockService.GetStockDataAsync($"{symbol.ToUpper()}.NS");

            if (stock == null)
            {
                await SendMessageAsync(chatId, $"❌ No data found for {symbol}");
                return;
            }

            var emoji = stock.ChangePercent > 0 ? "📈" : "📉";
            var message = $@"<b>{stock.Name} ({stock.Symbol})</b>

Price: ₹{stock.Price:F2} {emoji}
Change: {stock.ChangePercent:F2}%
Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}";

            await SendMessageAsync(chatId, message);
        }

        private async Task SubscribeUser(long chatId)
        {
            if (!_subscribedChats.Contains(chatId))
            {
                _subscribedChats.Add(chatId);
                await SendMessageAsync(chatId, "✅ Subscribed to daily updates!");
            }
            else
            {
                await SendMessageAsync(chatId, "You are already subscribed.");
            }
        }

        private async Task UnsubscribeUser(long chatId)
        {
            if (_subscribedChats.Contains(chatId))
            {
                _subscribedChats.Remove(chatId);
                await SendMessageAsync(chatId, "✅ Unsubscribed from updates.");
            }
        }

        private async Task SendDailyBriefing(long chatId)
        {
            using var scope = _scopeFactory.CreateScope();
            var briefingService = scope.ServiceProvider.GetRequiredService<IDailyBriefingService>();

            await SendMessageAsync(chatId, "📊 Generating your daily briefing... This may take a moment.");
            await briefingService.SendBriefingToUserAsync(chatId);
        }

        private async Task SubscribeToBriefing(long chatId)
        {
            // Store briefing preference (add to your user preferences)
            await SendMessageAsync(chatId, "✅ You'll receive daily briefings every morning at 8:30 AM.");
        }

        private async Task UnsubscribeFromBriefing(long chatId)
        {
            await SendMessageAsync(chatId, "✅ You've been unsubscribed from daily briefings.");
        }

        private async Task SendEnhancedAnalysis(long chatId)
        {
            using var scope = _scopeFactory.CreateScope();
            var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

            await SendMessageAsync(chatId, "🔍 Analyzing market data (last 10 days)... This may take a moment.");

            var report = await analysisService.GenerateDetailedReportAsync();
            await SendMessageAsync(chatId, report);
        }

        private async Task SendCategoryPicks(long chatId, string category)
        {
            using var scope = _scopeFactory.CreateScope();
            var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

            var picks = await analysisService.GetTopPicksByCategoryAsync(category, 5);

            var sb = new StringBuilder();
            sb.AppendLine($"<b>🏆 TOP {category.ToUpper()} CAP PICKS</b>\n");

            foreach (var pick in picks)
            {
                sb.AppendLine($"<b>{pick.Symbol}</b>");
                sb.AppendLine($"Target: ₹{pick.TargetPrice:F0} | Stop Loss: ₹{pick.StopLoss:F0}");
                sb.AppendLine($"Confidence: {pick.Confidence}%");
                sb.AppendLine($"Reason: {pick.Reason}\n");
            }

            await SendMessageAsync(chatId, sb.ToString());
        }

        private async Task SendTechnicalIndicators(long chatId)
        {
            using var scope = _scopeFactory.CreateScope();
            var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

            var indicators = await analysisService.GetMarketTechnicalIndicatorsAsync();

            var sb = new StringBuilder();
            sb.AppendLine("<b>📊 TECHNICAL INDICATORS</b>\n");

            foreach (var indicator in indicators)
            {
                sb.AppendLine($"<b>{indicator.Key}:</b> {indicator.Value}");
            }

            await SendMessageAsync(chatId, sb.ToString());
        }

        private async Task SendScheduledDailyReport()
        {
            using var scope = _scopeFactory.CreateScope();
            var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
            var aiService = scope.ServiceProvider.GetRequiredService<IAIService>();

            var stocks = await stockService.GetIndianStockDataAsync();
            var predictions = await aiService.GeneratePredictionsAsync(stocks);

            await SendDailyReportToAllAsync(predictions);
        }

        private string FormatDailyReport(DailyPredictionReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<b>📊 Daily Report - {report.Date:dd MMM yyyy}</b>\n");
            sb.AppendLine($"<i>{report.MarketSummary}</i>\n");

            foreach (var stock in report.Predictions.Take(5))
            {
                sb.AppendLine($"<b>{stock.Symbol}</b>: {stock.Recommendation} at ₹{stock.CurrentPrice:F2}");
            }

            return sb.ToString();
        }
    }
}