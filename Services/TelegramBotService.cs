using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StockNotificationApi.Services
{
    public class TelegramBotService : BackgroundService, ITelegramBotService
    {
        private readonly ILogger<TelegramBotService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;
        private TelegramBotClient? _botClient;
        private readonly List<long> _subscribedChats = new();
        private readonly List<long> _briefingSubscribers = new();

        // Track last command time to prevent spam
        private static readonly Dictionary<long, DateTime> _lastCommandTime = new();
        private static readonly Dictionary<long, string> _lastCommand = new();
        private static readonly Dictionary<long, int> _commandCount = new();
        private static readonly object _lockObject = new();

        // Command queue for processing
        private static readonly Dictionary<long, Queue<Func<Task>>> _commandQueues = new();
        private static readonly Dictionary<long, bool> _isProcessing = new();
        private static readonly SemaphoreSlim _queueSemaphore = new SemaphoreSlim(1, 1);

        // Constants
        private const int RATE_LIMIT_DELAY_MS = 100;
        private const int SCHEDULER_CHECK_INTERVAL_MS = 30000;
        private const int REPORT_SEND_DELAY_MS = 60000;
        private const int BRIEFING_HOUR = 8;
        private const int BRIEFING_MINUTE = 30;
        private const int REPORT_HOUR = 9;
        private const int REPORT_MINUTE = 0;
        private const int PRICE_COMMAND_PREFIX_LENGTH = 6;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 1000;

        // Rate limiting constants
        private const int COMMAND_COOLDOWN_SECONDS = 2;
        private const int MAX_COMMANDS_PER_MINUTE = 20;
        private const int RATE_LIMIT_WINDOW_MINUTES = 1;

        public TelegramBotService(
            ILogger<TelegramBotService> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        public async Task SendMessageAsync(long chatId, string message, ParseMode parseMode = ParseMode.Html)
        {
            try
            {
                if (_botClient == null)
                {
                    _logger.LogWarning("Bot client not initialized");
                    return;
                }

                if (string.IsNullOrEmpty(message))
                {
                    _logger.LogWarning("Attempted to send empty message to chat {ChatId}", chatId);
                    return;
                }

                await _botClient.SendMessage(chatId, message, parseMode: parseMode);
                _logger.LogDebug("Message sent to chat {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to chat {ChatId}", chatId);
            }
        }

        public async Task BroadcastToAllAsync(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                _logger.LogWarning("Attempted to broadcast empty message");
                return;
            }

            var subscribers = _subscribedChats.ToList();
            foreach (var chatId in subscribers)
            {
                await SendMessageAsync(chatId, message);
                await Task.Delay(RATE_LIMIT_DELAY_MS);
            }
            _logger.LogInformation("Broadcast sent to {Count} users", subscribers.Count);
        }

        public async Task SendDailyReportToAllAsync(DailyPredictionReport report)
        {
            if (report == null)
            {
                _logger.LogWarning("Attempted to send null report");
                return;
            }

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

            try
            {
                _botClient = new TelegramBotClient(token);
                _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, cancellationToken: stoppingToken);

                var me = await _botClient.GetMe();
                _logger.LogInformation("Bot started: @{Username}", me.Username);

                // Clean up rate limit tracking periodically
                _ = Task.Run(async () => await CleanupRateLimitTracking(stoppingToken), stoppingToken);

                // Main scheduler loop
                while (!stoppingToken.IsCancellationRequested)
                {
                    var now = DateTime.Now;

                    if (now.Hour == REPORT_HOUR && now.Minute == REPORT_MINUTE)
                    {
                        await SendScheduledDailyReport();
                        await Task.Delay(REPORT_SEND_DELAY_MS, stoppingToken);
                    }

                    if (now.Hour == BRIEFING_HOUR && now.Minute == BRIEFING_MINUTE)
                    {
                        await SendScheduledDailyBriefing();
                        await Task.Delay(REPORT_SEND_DELAY_MS, stoppingToken);
                    }

                    await Task.Delay(SCHEDULER_CHECK_INTERVAL_MS, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Telegram bot service is stopping");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Telegram bot execution");
            }
        }

        private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken token)
        {
            if (update.Message?.Chat?.Id == null)
                return;

            var chatId = update.Message.Chat.Id;

            try
            {
                if (string.IsNullOrEmpty(update.Message.Text))
                    return;

                var text = update.Message.Text.Trim();
                var lowerText = text.ToLower();

                _logger.LogInformation("Received message: {Text} from {ChatId}", text, chatId);

                // CHECK RATE LIMIT
                if (IsRateLimited(chatId))
                {
                    await SendMessageAsync(chatId, "⏳ You're sending commands too quickly. Please wait a moment.");
                    return;
                }

                // UPDATE RATE LIMIT TRACKING
                UpdateRateLimit(chatId, text);

                // SEND TYPING ACTION
                await SendTypingAction(chatId);

                // SEND IMMEDIATE ACKNOWLEDGEMENT
                await SendImmediateAcknowledgment(chatId, lowerText, text);

                // QUEUE THE COMMAND FOR PROCESSING
                await QueueCommand(chatId, async () =>
                {
                    try
                    {
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
                            case "/trades":
                                await SendTodaysTrades(chatId);
                                break;
                            case "/toptrades":
                                await SendTopTrades(chatId, 5);
                                break;
                            case "/execute_trades":
                                await ExecuteTodaysTrades(chatId);
                                break;
                            case "/portfolio_status":
                                await SendPortfolioStatus(chatId);
                                break;
                            case "/optimize_holdings":
                                await OptimizeHoldings(chatId);
                                break;
                            default:
                                await HandlePriceCommand(chatId, text, lowerText);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing queued command for chat {ChatId}", chatId);
                        await SendMessageAsync(chatId, "❌ Error processing command. Please try again.");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update for chat {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ An error occurred. Please try again.");
            }
        }

        /// <summary>
        /// Sends immediate acknowledgment message based on command type
        /// </summary>
        private async Task SendImmediateAcknowledgment(long chatId, string lowerText, string originalText)
        {
            try
            {
                if (lowerText == "/stocks")
                    await SendMessageAsync(chatId, "🔄 Fetching top stocks... Please wait.");
                else if (lowerText == "/topgainers")
                    await SendMessageAsync(chatId, "🔄 Fetching top gainers... Please wait.");
                else if (lowerText == "/toplosers")
                    await SendMessageAsync(chatId, "🔄 Fetching top losers... Please wait.");
                else if (lowerText == "/trades")
                    await SendMessageAsync(chatId, "🔄 Fetching today's trading signals... Please wait.");
                else if (lowerText == "/toptrades")
                    await SendMessageAsync(chatId, "🔄 Fetching top trades... Please wait.");
                else if (lowerText == "/largecap")
                    await SendMessageAsync(chatId, "🔄 Fetching large cap picks... Please wait.");
                else if (lowerText == "/midcap")
                    await SendMessageAsync(chatId, "🔄 Fetching mid cap picks... Please wait.");
                else if (lowerText == "/smallcap")
                    await SendMessageAsync(chatId, "🔄 Fetching small cap picks... Please wait.");
                else if (lowerText == "/analysis")
                    await SendMessageAsync(chatId, "🔍 Analyzing market data (last 10 days)... This may take a moment.");
                else if (lowerText == "/technical")
                    await SendMessageAsync(chatId, "🔄 Fetching technical indicators... Please wait.");
                else if (lowerText == "/briefing")
                    await SendMessageAsync(chatId, "📊 Generating your daily briefing... This may take a moment.");
                else if (lowerText == "/subscribe")
                    await SendMessageAsync(chatId, "🔄 Processing your subscription...");
                else if (lowerText == "/unsubscribe")
                    await SendMessageAsync(chatId, "🔄 Processing your unsubscription...");
                else if (lowerText == "/subscribe_briefing")
                    await SendMessageAsync(chatId, "🔄 Processing briefing subscription...");
                else if (lowerText == "/unsubscribe_briefing")
                    await SendMessageAsync(chatId, "🔄 Processing briefing unsubscription...");
                else if (lowerText == "/execute_trades")
                    await SendMessageAsync(chatId, "🔄 Analyzing and executing trades... Please wait.");
                else if (lowerText == "/portfolio_status")
                    await SendMessageAsync(chatId, "🔄 Fetching your portfolio status... Please wait.");
                else if (lowerText == "/optimize_holdings")
                    await SendMessageAsync(chatId, "🔄 Optimizing your holdings... Please wait.");
                else if (lowerText.StartsWith("/price"))
                {
                    var symbol = originalText.Substring(PRICE_COMMAND_PREFIX_LENGTH).Trim();
                    if (!string.IsNullOrEmpty(symbol))
                        await SendMessageAsync(chatId, $"🔄 Fetching price data for {symbol}... Please wait.");
                    else
                        await SendTypingAction(chatId);
                }
                else if (lowerText == "/start" || lowerText == "/help")
                    await SendTypingAction(chatId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send acknowledgment message");
            }
        }

        private async Task HandlePriceCommand(long chatId, string text, string lowerText)
        {
            if (lowerText.StartsWith("/price"))
            {
                if (text.Length <= PRICE_COMMAND_PREFIX_LENGTH)
                {
                    await SendMessageAsync(chatId, "❌ Please provide a symbol. Example: /price RELIANCE");
                    return;
                }

                var symbol = text.Substring(PRICE_COMMAND_PREFIX_LENGTH).Trim();
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    await SendMessageAsync(chatId, "❌ Please provide a symbol. Example: /price RELIANCE");
                    return;
                }

                await SendStockPrice(chatId, symbol.ToUpper());
            }
            else
            {
                await SendMessageAsync(chatId, "❌ Unknown command. Type /help for available commands.");
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
        {
            _logger.LogError(exception, "Telegram bot error");
            return Task.CompletedTask;
        }

        #region Command Queue Management

        private async Task QueueCommand(long chatId, Func<Task> command)
        {
            await _queueSemaphore.WaitAsync();
            try
            {
                if (!_commandQueues.ContainsKey(chatId))
                {
                    _commandQueues[chatId] = new Queue<Func<Task>>();
                }

                _commandQueues[chatId].Enqueue(command);

                if (!_isProcessing.ContainsKey(chatId) || !_isProcessing[chatId])
                {
                    _isProcessing[chatId] = true;
                    _ = Task.Run(() => ProcessCommandQueue(chatId));
                }
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }

        private async Task ProcessCommandQueue(long chatId)
        {
            while (true)
            {
                Func<Task>? command = null;

                await _queueSemaphore.WaitAsync();
                try
                {
                    if (_commandQueues.ContainsKey(chatId) && _commandQueues[chatId].Count > 0)
                    {
                        command = _commandQueues[chatId].Dequeue();
                    }
                    else
                    {
                        _isProcessing[chatId] = false;
                        break;
                    }
                }
                finally
                {
                    _queueSemaphore.Release();
                }

                if (command != null)
                {
                    try
                    {
                        await command();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing queued command for chat {ChatId}", chatId);
                        await SendMessageAsync(chatId, "❌ Error processing command. Please try again.");
                    }

                    // Small delay between commands in the queue
                    await Task.Delay(500);
                }
            }
        }

        #endregion

        #region Rate Limiting Methods

        private async Task SendTypingAction(long chatId)
        {
            try
            {
                if (_botClient == null) return;
                await _botClient.SendChatAction(chatId, ChatAction.Typing);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send typing action");
            }
        }

        private bool IsRateLimited(long chatId)
        {
            lock (_lockObject)
            {
                if (_commandCount.TryGetValue(chatId, out var count))
                {
                    return count >= MAX_COMMANDS_PER_MINUTE;
                }
                return false;
            }
        }

        private void UpdateRateLimit(long chatId, string command)
        {
            lock (_lockObject)
            {
                _lastCommandTime[chatId] = DateTime.UtcNow;
                _lastCommand[chatId] = command;

                if (_commandCount.ContainsKey(chatId))
                    _commandCount[chatId]++;
                else
                    _commandCount[chatId] = 1;
            }
        }

        private async Task CleanupRateLimitTracking(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(RATE_LIMIT_WINDOW_MINUTES), stoppingToken);

                lock (_lockObject)
                {
                    _commandCount.Clear();
                    _logger.LogDebug("Cleared rate limit tracking");
                }
            }
        }

        #endregion

        #region Portfolio Management Methods

        /// <summary>
        /// Sends portfolio status to user
        /// </summary>
        private async Task SendPortfolioStatus(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();

                var portfolio = await angelOneService.GetPortfolioAsync();

                if (portfolio?.Holdings == null || !portfolio.Holdings.Any())
                {
                    await SendMessageAsync(chatId, "📊 Your portfolio is empty.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>📊 PORTFOLIO SUMMARY</b>");
                sb.AppendLine($"As of: {portfolio.AsOfDate:dd MMM yyyy HH:mm}\n");

                sb.AppendLine($"Total Investment: ₹{portfolio.TotalInvestment:N2}");
                sb.AppendLine($"Current Value: ₹{portfolio.CurrentValue:N2}");
                sb.AppendLine($"Total P&L: {(portfolio.TotalProfitLoss >= 0 ? "🟢" : "🔴")} ₹{Math.Abs(portfolio.TotalProfitLoss):N2} ({portfolio.TotalProfitLossPercent:F2}%)\n");

                sb.AppendLine("<b>HOLDINGS:</b>");

                foreach (var holding in portfolio.Holdings.Take(10))
                {
                    var plEmoji = holding.ProfitLoss >= 0 ? "🟢" : "🔴";
                    sb.AppendLine($"• {holding.Symbol}: {holding.Quantity} shares @ ₹{holding.AveragePrice:F2}");
                    sb.AppendLine($"  Current: ₹{holding.CurrentPrice:F2} {plEmoji} P&L: ₹{holding.ProfitLoss:N2}");
                }

                if (portfolio.Holdings.Count > 10)
                {
                    sb.AppendLine($"... and {portfolio.Holdings.Count - 10} more holdings");
                }

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending portfolio status to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching portfolio status. Please try again later.");
            }
        }

        /// <summary>
        /// Optimizes holdings based on current market conditions
        /// </summary>
        private async Task OptimizeHoldings(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var optimizer = scope.ServiceProvider.GetRequiredService<HoldingOptimizer>();

                await SendMessageAsync(chatId, "🔄 Analyzing your holdings for optimization...");

                var plan = await optimizer.AnalyzeHoldingsAsync();

                if (plan?.Actions == null || !plan.Actions.Any())
                {
                    await SendMessageAsync(chatId, "✅ Your portfolio is already optimized! No actions needed.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("<b>📊 OPTIMIZATION PLAN</b>\n");

                foreach (var action in plan.Actions.OrderBy(a => a.Priority))
                {
                    var emoji = action.Action switch
                    {
                        "BUY" => "🟢",
                        "SELL" => "🔴",
                        _ => "⚪"
                    };

                    sb.AppendLine($"{emoji} <b>{action.Action} {action.Quantity} {action.Symbol}</b>");
                    sb.AppendLine($"   Reason: {action.Reason}");
                    if (action.ExpectedProfit > 0)
                    {
                        sb.AppendLine($"   Expected Profit: ₹{action.ExpectedProfit:N2}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine($"<b>Total Actions: {plan.Actions.Count}</b>");
                sb.AppendLine($"Expected Impact: ₹{plan.TotalImpact:N2}");

                await SendMessageAsync(chatId, sb.ToString());

                // Ask for confirmation
                await SendMessageAsync(chatId, "❓ Would you like to execute these optimizations? Use /execute_trades to proceed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing holdings for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error optimizing holdings. Please try again later.");
            }
        }

        /// <summary>
        /// Executes today's trades based on trading signals
        /// </summary>
        private async Task ExecuteTodaysTrades(long chatId)
        {
            await SendMessageAsync(chatId, "🔄 Analyzing and executing today's trades...");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var autoTrading = scope.ServiceProvider.GetRequiredService<AutoTradingService>();
                var optimizer = scope.ServiceProvider.GetRequiredService<HoldingOptimizer>();

                // First, analyze current holdings
                var plan = await optimizer.AnalyzeHoldingsAsync();

                if (plan?.Actions == null || !plan.Actions.Any())
                {
                    await SendMessageAsync(chatId, "📊 No trades to execute at this time.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("<b>📊 TRADING PLAN</b>\n");

                foreach (var action in plan.Actions.OrderBy(a => a.Priority))
                {
                    var emoji = action.Action == "BUY" ? "🟢" : action.Action == "SELL" ? "🔴" : "⚪";
                    sb.AppendLine($"{emoji} {action.Action} {action.Quantity} {action.Symbol}");
                    sb.AppendLine($"   Reason: {action.Reason}");
                    sb.AppendLine($"   Expected Price: ₹{action.Price:F2}");
                    sb.AppendLine();
                }

                sb.AppendLine($"<b>Total Trades: {plan.Actions.Count}</b>");
                sb.AppendLine($"Expected Portfolio Impact: ₹{plan.TotalImpact:N2}");

                await SendMessageAsync(chatId, sb.ToString());

                // In production, you would implement actual trade execution here
                await SendMessageAsync(chatId, "⚠️ Auto-execution is disabled. Please execute trades manually through your broker.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing trades for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error executing trades. Please try again later.");
            }
        }

        #endregion

        #region Command Handlers

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
/portfolio_status - View your portfolio
/optimize_holdings - Optimize your holdings

<b>Get started by trying /stocks or /topgainers!</b>";

            await SendMessageAsync(chatId, message);
        }

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

<b>📈 Enhanced Analysis:</b>
/analysis - Complete 10-day analysis & prediction
/largecap - Top large cap picks
/midcap - Top mid cap picks
/smallcap - Top small cap picks
/technical - Technical indicators

<b>💰 Trading Signals:</b>
/trades - Today's trading recommendations
/toptrades - Top 5 trades by confidence

<b>📊 Portfolio Management:</b>
/portfolio_status - View your current portfolio
/optimize_holdings - Get optimization suggestions
/execute_trades - Execute trading plan

<b>Subscription:</b>
/subscribe - Get daily updates
/unsubscribe - Stop updates";

            await SendMessageAsync(chatId, message);
        }

        private async Task SendTopStocks(long chatId)
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

                var topStocks = stocks
                    .Where(s => s != null)
                    .OrderByDescending(s => Math.Abs(s.ChangePercent))
                    .Take(10)
                    .ToList();

                var message = new StringBuilder();
                message.AppendLine("<b>🏆 Top 10 Active Stocks</b>\n");

                foreach (var stock in topStocks)
                {
                    var arrow = stock.ChangePercent > 0 ? "📈" : "📉";
                    var emoji = stock.ChangePercent > 0 ? "🟢" : "🔴";
                    message.AppendLine($"{emoji} {stock.Symbol}: ₹{stock.Price:F2} {arrow} {Math.Abs(stock.ChangePercent):F2}%");
                }

                await SendMessageAsync(chatId, message.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top stocks");
                await SendMessageAsync(chatId, "❌ Error fetching stock data.");
            }
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

                var gainers = stocks
                    .Where(s => s != null && s.ChangePercent > 0)
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
                    message.AppendLine($"🟢 {stock.Symbol}: ₹{stock.Price:F2} 📈 +{stock.ChangePercent:F2}%");
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

                var losers = stocks
                    .Where(s => s != null && s.ChangePercent < 0)
                    .OrderBy(s => s.ChangePercent)
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
                    message.AppendLine($"🔴 {stock.Symbol}: ₹{stock.Price:F2} 📉 {stock.ChangePercent:F2}%");
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

        private async Task SendStockPrice(long chatId, string symbol)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                var stock = await stockService.GetStockDataAsync($"{symbol}.NS");
                if (stock == null)
                {
                    stock = await stockService.GetStockDataAsync($"{symbol}.BO");
                }

                if (stock == null)
                {
                    await SendMessageAsync(chatId, $"❌ No data found for {symbol}");
                    return;
                }

                var emoji = stock.ChangePercent > 0 ? "📈" : "📉";
                var colorEmoji = stock.ChangePercent > 0 ? "🟢" : "🔴";
                var message = $@"{colorEmoji} <b>{stock.Name} ({stock.Symbol})</b>

Price: ₹{stock.Price:F2} {emoji}
Change: {stock.ChangePercent:F2}%
Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}
Volume: {stock.Volume:N0}";

                await SendMessageAsync(chatId, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending stock price for {Symbol}", symbol);
                await SendMessageAsync(chatId, $"❌ Error fetching data for {symbol}");
            }
        }

        private async Task SubscribeUser(long chatId)
        {
            if (!_subscribedChats.Contains(chatId))
            {
                _subscribedChats.Add(chatId);
                await SendMessageAsync(chatId, "✅ Subscribed to daily updates! You'll receive reports every morning at 9 AM.");
            }
            else
            {
                await SendMessageAsync(chatId, "You are already subscribed to daily updates.");
            }
        }

        private async Task UnsubscribeUser(long chatId)
        {
            if (_subscribedChats.Contains(chatId))
            {
                _subscribedChats.Remove(chatId);
                await SendMessageAsync(chatId, "✅ Unsubscribed from daily updates.");
            }
            else
            {
                await SendMessageAsync(chatId, "You are not currently subscribed.");
            }
        }

        private async Task SendDailyBriefing(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var briefingService = scope.ServiceProvider.GetRequiredService<IDailyBriefingService>();

                await briefingService.SendBriefingToUserAsync(chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending daily briefing to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error generating briefing. Please try again later.");
            }
        }

        private async Task SubscribeToBriefing(long chatId)
        {
            if (!_briefingSubscribers.Contains(chatId))
            {
                _briefingSubscribers.Add(chatId);
                await SendMessageAsync(chatId, "✅ You'll receive daily briefings every morning at 8:30 AM.");
            }
            else
            {
                await SendMessageAsync(chatId, "You are already subscribed to daily briefings.");
            }
        }

        private async Task UnsubscribeFromBriefing(long chatId)
        {
            if (_briefingSubscribers.Contains(chatId))
            {
                _briefingSubscribers.Remove(chatId);
                await SendMessageAsync(chatId, "✅ You've been unsubscribed from daily briefings.");
            }
            else
            {
                await SendMessageAsync(chatId, "You are not subscribed to daily briefings.");
            }
        }

        private async Task SendEnhancedAnalysis(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

                var report = await analysisService.GenerateDetailedReportAsync();
                await SendMessageAsync(chatId, report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending enhanced analysis to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error generating analysis. Please try again later.");
            }
        }

        private async Task SendCategoryPicks(long chatId, string category)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

                var picks = await analysisService.GetTopPicksByCategoryAsync(category, 5);

                if (picks == null || !picks.Any())
                {
                    await SendMessageAsync(chatId, $"❌ No picks available for {category} cap.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>🏆 TOP {category.ToUpper()} CAP PICKS</b>\n");

                foreach (var pick in picks.Where(p => p != null))
                {
                    sb.AppendLine($"<b>{pick.Symbol}</b>");
                    sb.AppendLine($"Target: ₹{pick.TargetPrice:F0} | Stop Loss: ₹{pick.StopLoss:F0}");
                    sb.AppendLine($"Confidence: {pick.Confidence}%");
                    sb.AppendLine($"Reason: {pick.Reason}\n");
                }

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending {Category} picks to {ChatId}", category, chatId);
                await SendMessageAsync(chatId, $"❌ Error fetching {category} cap picks.");
            }
        }

        private async Task SendTechnicalIndicators(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

                var indicators = await analysisService.GetMarketTechnicalIndicatorsAsync();

                var sb = new StringBuilder();
                sb.AppendLine("<b>📊 TECHNICAL INDICATORS</b>\n");

                foreach (var indicator in indicators)
                {
                    sb.AppendLine($"<b>{indicator.Key.ToUpper()}:</b> {indicator.Value}");
                }

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending technical indicators to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching technical indicators.");
            }
        }

        private async Task SendTodaysTrades(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();

                var dashboard = await tradingService.GetTodaysTradesAsync(minConfidence: 70);

                if (dashboard?.Trades == null || !dashboard.Trades.Any())
                {
                    await SendMessageAsync(chatId, "📊 No profitable trades available today.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>📊 TODAY'S TRADING SIGNALS</b>");
                sb.AppendLine($"Market: {dashboard.MarketPhase} | Confidence: {dashboard.MarketConfidence}%\n");

                sb.AppendLine($"<b>🎯 TOP PICKS</b>");

                foreach (var trade in dashboard.Trades.Take(5))
                {
                    var emoji = trade.Confidence >= 80 ? "🔴" : trade.Confidence >= 70 ? "🟡" : "⚪";
                    var arrow = trade.PotentialReturn > 0 ? "📈" : "📉";

                    sb.AppendLine($"{emoji} <b>{trade.Symbol}</b> ({trade.Category}) {arrow}");
                    sb.AppendLine($"   Entry: ₹{trade.CurrentPrice:F2}");
                    sb.AppendLine($"   Target: ₹{trade.TargetPrice:F0} | SL: ₹{trade.StopLoss:F0}");
                    sb.AppendLine($"   Potential: +{trade.PotentialReturn:F1}% | R/R: {trade.RiskReward}:1");
                    sb.AppendLine($"   <i>{trade.Reason}</i>\n");
                }

                sb.AppendLine($"<b>📈 SUMMARY</b>");
                sb.AppendLine($"Total Trades: {dashboard.Summary.TotalTrades}");
                sb.AppendLine($"High Confidence: {dashboard.Summary.HighConfidenceTrades}");
                sb.AppendLine($"Avg Confidence: {dashboard.Summary.AverageConfidence:F1}%");
                sb.AppendLine($"Avg Return: +{dashboard.Summary.TotalPotentialReturn:F1}%");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending trades to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching trading signals");
            }
        }

        private async Task SendTopTrades(long chatId, int count)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();

                var topTrades = await tradingService.GetTopTradesAsync(count);

                if (topTrades == null || !topTrades.Any())
                {
                    await SendMessageAsync(chatId, "📊 No top trades available today.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>🏆 TOP {count} TRADES TODAY</b>\n");

                foreach (var trade in topTrades)
                {
                    var emoji = trade.Confidence >= 80 ? "🔴" : trade.Confidence >= 70 ? "🟡" : "⚪";
                    var arrow = trade.PotentialReturn > 0 ? "📈" : "📉";

                    sb.AppendLine($"{emoji} <b>{trade.Symbol}</b> ({trade.Category}) {arrow}");
                    sb.AppendLine($"   Entry: ₹{trade.CurrentPrice:F2}");
                    sb.AppendLine($"   Target: ₹{trade.TargetPrice:F0} | SL: ₹{trade.StopLoss:F0}");
                    sb.AppendLine($"   Potential: +{trade.PotentialReturn:F1}% | R/R: {trade.RiskReward}:1");
                    sb.AppendLine($"   <i>{trade.Reason}</i>");
                    sb.AppendLine();
                }

                var avgConfidence = topTrades.Average(t => t.Confidence);
                var avgReturn = topTrades.Average(t => t.PotentialReturn);
                var bestRR = topTrades.Max(t => t.RiskReward);

                sb.AppendLine($"<b>📊 SUMMARY</b>");
                sb.AppendLine($"Avg Confidence: {avgConfidence:F1}%");
                sb.AppendLine($"Avg Return: +{avgReturn:F1}%");
                sb.AppendLine($"Best R/R: {bestRR}:1");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top trades to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching top trades");
            }
        }

        private async Task SendScheduledDailyReport()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
                var aiService = scope.ServiceProvider.GetRequiredService<IAIService>();

                var stocks = await stockService.GetIndianStockDataAsync();
                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stocks data available for daily report");
                    return;
                }

                var predictions = await aiService.GeneratePredictionsAsync(stocks);
                predictions.MarketSummary = await aiService.GetMarketInsightAsync(stocks);

                await SendDailyReportToAllAsync(predictions);
                _logger.LogInformation("Daily report sent to all subscribers");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending scheduled daily report");
            }
        }

        private async Task SendScheduledDailyBriefing()
        {
            try
            {
                if (!_briefingSubscribers.Any())
                {
                    _logger.LogInformation("No briefing subscribers to send to");
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var briefingService = scope.ServiceProvider.GetRequiredService<IDailyBriefingService>();

                foreach (var chatId in _briefingSubscribers.ToList())
                {
                    await briefingService.SendBriefingToUserAsync(chatId);
                    await Task.Delay(RATE_LIMIT_DELAY_MS);
                }

                _logger.LogInformation("Daily briefing sent to {Count} subscribers", _briefingSubscribers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending scheduled daily briefing");
            }
        }

        private string FormatDailyReport(DailyPredictionReport report)
        {
            if (report == null) return "No report available.";

            var sb = new StringBuilder();
            sb.AppendLine($"<b>📊 Daily Report - {report.Date:dd MMM yyyy}</b>\n");

            if (!string.IsNullOrEmpty(report.MarketSummary))
            {
                sb.AppendLine($"<i>{report.MarketSummary}</i>\n");
            }

            if (!string.IsNullOrEmpty(report.TopPick))
            {
                sb.AppendLine($"🏆 <b>Top Pick:</b> {report.TopPick}\n");
            }

            foreach (var stock in report.Predictions.Where(p => p != null).Take(5))
            {
                var emoji = stock.Recommendation?.ToLower() switch
                {
                    "buy" => "🟢",
                    "sell" => "🔴",
                    "hold" => "🟡",
                    _ => "⚪"
                };
                sb.AppendLine($"{emoji} <b>{stock.Symbol}</b>: {stock.Recommendation ?? "Hold"} at ₹{stock.CurrentPrice:F2}");
            }

            return sb.ToString();
        }

        #endregion
    }
}