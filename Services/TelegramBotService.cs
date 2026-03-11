using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;
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
        private readonly IPriceAlertService _priceAlertService;
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
        private const int ALERT_COMMAND_PREFIX_LENGTH = 6;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 1000;

        // Rate limiting constants
        private const int COMMAND_COOLDOWN_SECONDS = 2;
        private const int MAX_COMMANDS_PER_MINUTE = 20;
        private const int RATE_LIMIT_WINDOW_MINUTES = 1;

        public TelegramBotService(
            ILogger<TelegramBotService> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory,
            IPriceAlertService priceAlertService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _priceAlertService = priceAlertService ?? throw new ArgumentNullException(nameof(priceAlertService));
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
                            case "/alerts":
                                await ShowAlerts(chatId);
                                break;
                            case "/clearalerts":
                                await ClearAlerts(chatId);
                                break;
                            case "/market":
                                await SendMarketStatus(chatId);
                                break;
                            case "/suggest":
                                await SendPortfolioSuggestions(chatId);
                                break;
                            default:
                                if (lowerText.StartsWith("/alert"))
                                    await HandleAlertCommand(chatId, text);
                                else if (lowerText.StartsWith("/price"))
                                    await HandlePriceCommand(chatId, text);
                                else if (lowerText.StartsWith("/removealert"))
                                    await HandleRemoveAlertCommand(chatId, text);
                                else
                                    await SendMessageAsync(chatId, "❌ Unknown command. Type /help for available commands.");
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
                else if (lowerText == "/alerts")
                    await SendMessageAsync(chatId, "🔄 Fetching your alerts... Please wait.");
                else if (lowerText == "/clearalerts")
                    await SendMessageAsync(chatId, "🔄 Clearing triggered alerts...");
                else if (lowerText == "/suggest")
                    await SendMessageAsync(chatId, "🔄 Analyzing your portfolio... This may take a moment.");
                else if (lowerText.StartsWith("/alert"))
                {
                    var symbol = originalText.Length > ALERT_COMMAND_PREFIX_LENGTH
                        ? originalText.Substring(ALERT_COMMAND_PREFIX_LENGTH).Split(' ').FirstOrDefault()?.Trim()
                        : null;
                    if (!string.IsNullOrEmpty(symbol))
                        await SendMessageAsync(chatId, $"🔄 Setting price alert for {symbol}... Please wait.");
                    else
                        await SendTypingAction(chatId);
                }
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

        // ========== PORTFOLIO SUGGESTIONS ENHANCEMENTS ==========

        private async Task SendPortfolioSuggestions(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var optimizer = scope.ServiceProvider.GetRequiredService<HoldingOptimizer>();
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();

                var holdings = await angelOneService.GetHoldingsAsync();

                if (holdings == null || !holdings.Any())
                {
                    await SendMessageAsync(chatId, "📊 You don't have any holdings in your portfolio yet.\n\nStart investing to see improvement suggestions!");
                    return;
                }

                var plan = await optimizer.AnalyzeHoldingsAsync();
                var portfolio = await angelOneService.GetPortfolioAsync();

                // Calculate sector exposure
                var sectorExposure = CalculateSectorAnalysis(holdings);

                // Enhanced Health Score
                int healthScore = CalculateEnhancedHealthScore(holdings, portfolio.CurrentValue, portfolio.TotalInvestment, sectorExposure);

                // Enhanced Warnings
                var warnings = GenerateEnhancedWarnings(holdings, portfolio, sectorExposure);

                // Send overview
                var overview = new StringBuilder();
                overview.AppendLine("📊 <b>PORTFOLIO IMPROVEMENT SUGGESTIONS</b>\n");

                var healthEmoji = healthScore >= 80 ? "🟢" : healthScore >= 60 ? "🟡" : "🔴";
                overview.AppendLine($"{healthEmoji} <b>Portfolio Health Score:</b> {healthScore}/100\n");
                overview.AppendLine($"💰 <b>Total Value:</b> ₹{portfolio.CurrentValue:N2}");
                overview.AppendLine($"📈 <b>Total Investment:</b> ₹{portfolio.TotalInvestment:N2}");

                var pnlEmoji = portfolio.TotalProfitLoss >= 0 ? "🟢" : "🔴";
                overview.AppendLine($"{pnlEmoji} <b>Total P&L:</b> ₹{Math.Abs(portfolio.TotalProfitLoss):N2} ({portfolio.TotalProfitLossPercent:F2}%)\n");

                if (warnings.Any())
                {
                    overview.AppendLine("<b>⚠️ Key Warnings:</b>");
                    foreach (var warning in warnings.Take(5))
                    {
                        overview.AppendLine($"• {warning}");
                    }
                    overview.AppendLine();
                }

                await SendMessageAsync(chatId, overview.ToString());

                // Send action items from optimizer
                var actionableTrades = plan.Actions.Where(a => a.Action != "HOLD").ToList();
                if (actionableTrades.Any())
                {
                    var actions = new StringBuilder();
                    actions.AppendLine("<b>📋 RECOMMENDED ACTIONS</b>\n");

                    var sells = actionableTrades.Where(a => a.Action == "SELL").OrderBy(a => a.Priority).ToList();
                    if (sells.Any())
                    {
                        actions.AppendLine("<b>🔴 Sell Recommendations:</b>");
                        foreach (var sell in sells.Take(5))
                        {
                            var priorityEmoji = sell.Priority == 1 ? "⚠️" : "📉";
                            actions.AppendLine($"{priorityEmoji} <b>{sell.Symbol}</b>: Sell {sell.Quantity} shares");
                            actions.AppendLine($"   Reason: {sell.Reason}");
                            if (sell.ExpectedProfit != 0)
                            {
                                var profitEmoji = sell.ExpectedProfit > 0 ? "🟢" : "🔴";
                                actions.AppendLine($"   Expected: {profitEmoji} ₹{Math.Abs(sell.ExpectedProfit):N2}");
                            }
                            actions.AppendLine();
                        }
                    }

                    await SendMessageAsync(chatId, actions.ToString());
                }

                // Buy Recommendations based on available cash
                var buyRecommendations = await GenerateBuyRecommendations(holdings, tradingService, portfolio);
                if (buyRecommendations.Any())
                {
                    var buyMsg = new StringBuilder();
                    buyMsg.AppendLine("<b>🟢 BUY RECOMMENDATIONS</b>\n");

                    var cashPosition = portfolio.CurrentValue - portfolio.TotalInvestment;
                    if (cashPosition > 0)
                    {
                        buyMsg.AppendLine($"<i>Based on available cash: ₹{cashPosition:N2}</i>\n");
                    }
                    else
                    {
                        buyMsg.AppendLine($"<i>Note: You're currently in loss. Consider these buys when you add funds.</i>\n");
                    }

                    foreach (var rec in buyRecommendations)
                    {
                        var totalCost = rec.Quantity * rec.CurrentPrice;
                        buyMsg.AppendLine($"• <b>{rec.Symbol}</b> ({rec.Category})");
                        buyMsg.AppendLine($"  Buy {rec.Quantity} shares @ ₹{rec.CurrentPrice:F2} = ₹{totalCost:N2}");
                        buyMsg.AppendLine($"  Target: ₹{rec.TargetPrice:F0} | SL: ₹{rec.StopLoss:F0}");
                        buyMsg.AppendLine($"  Confidence: {rec.Confidence}% | R/R: {rec.RiskReward}:1");
                        buyMsg.AppendLine($"  <i>{rec.Reason}</i>\n");
                    }
                    await SendMessageAsync(chatId, buyMsg.ToString());
                }

                // Rebalancing Suggestions
                var rebalanceSuggestions = GenerateRebalanceSuggestions(holdings, portfolio, sectorExposure);
                if (rebalanceSuggestions.Any())
                {
                    var rebalanceMsg = new StringBuilder();
                    rebalanceMsg.AppendLine("<b>⚖️ REBALANCING SUGGESTIONS</b>\n");

                    foreach (var suggestion in rebalanceSuggestions)
                    {
                        rebalanceMsg.AppendLine($"• {suggestion}");
                    }
                    await SendMessageAsync(chatId, rebalanceMsg.ToString());
                }

                // Send sector analysis
                if (sectorExposure.Any())
                {
                    var sector = new StringBuilder();
                    sector.AppendLine("<b>📊 SECTOR ALLOCATION</b>\n");

                    foreach (var sec in sectorExposure.OrderByDescending(s => s.Value).Take(5))
                    {
                        var emoji = sec.Value > 40 ? "🔴" : sec.Value > 25 ? "🟡" : "🟢";
                        sector.AppendLine($"{emoji} {sec.Key}: {sec.Value:F1}%");
                    }

                    await SendMessageAsync(chatId, sector.ToString());
                }

                if (actionableTrades.Any() || buyRecommendations.Any() || rebalanceSuggestions.Any())
                {
                    await SendMessageAsync(chatId,
                        "❓ To implement these suggestions, use:\n" +
                        "• /sell SYMBOL QUANTITY - Sell specific stock\n" +
                        "• /buy SYMBOL QUANTITY PRICE - Buy specific stock");
                }
            }
            catch (UnauthorizedAccessException)
            {
                await SendMessageAsync(chatId, "🔑 Please connect your Angel One account first.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending portfolio suggestions to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error generating portfolio suggestions. Please try again later.");
            }
        }

        private int CalculateEnhancedHealthScore(List<Holding> holdings, decimal totalValue, decimal totalInvestment, Dictionary<string, decimal> sectorExposure)
        {
            if (holdings == null || !holdings.Any())
                return 0;

            int score = 70; // Start with base score
            var totalPL = totalValue - totalInvestment;
            var plPercentage = totalInvestment > 0 ? (totalPL / totalInvestment) * 100 : 0;

            // PROFITABILITY (40 points)
            if (plPercentage > 20)
                score += 20;
            else if (plPercentage > 10)
                score += 15;
            else if (plPercentage > 0)
                score += 10;
            else if (plPercentage > -10)
                score -= 5;
            else if (plPercentage > -20)
                score -= 15;
            else
                score -= 25;

            // DIVERSIFICATION (30 points)
            if (holdings.Count >= 8 && holdings.Count <= 15)
                score += 15;
            else if (holdings.Count >= 5 && holdings.Count < 8)
                score += 10;
            else if (holdings.Count > 15 && holdings.Count <= 20)
                score += 5;
            else if (holdings.Count < 5)
                score -= 10;
            else if (holdings.Count > 20)
                score -= 5;

            // Check for over-concentration
            foreach (var holding in holdings)
            {
                var percentage = (holding.Quantity * holding.CurrentPrice / totalValue) * 100;
                if (percentage > 30)
                    score -= 15;
                else if (percentage > 20)
                    score -= 10;
                else if (percentage > 15)
                    score -= 5;
            }

            // RISK MANAGEMENT (20 points)
            var losers = holdings.Count(h => h.ProfitLoss < 0);
            var loserRatio = holdings.Count > 0 ? (double)losers / holdings.Count : 0;

            if (loserRatio < 0.2)
                score += 15;
            else if (loserRatio < 0.4)
                score += 10;
            else if (loserRatio < 0.6)
                score += 5;
            else if (loserRatio > 0.8)
                score -= 15;
            else if (loserRatio > 0.6)
                score -= 10;

            // SECTOR DIVERSIFICATION (10 points)
            if (sectorExposure.Count >= 5)
                score += 10;
            else if (sectorExposure.Count >= 3)
                score += 5;
            else if (sectorExposure.Count <= 1)
                score -= 10;

            return Math.Max(0, Math.Min(100, score));
        }

        private List<string> GenerateEnhancedWarnings(List<Holding> holdings, PortfolioSummary portfolio, Dictionary<string, decimal> sectorExposure)
        {
            var warnings = new List<string>();

            if (holdings == null || !holdings.Any())
                return warnings;

            var totalValue = portfolio.CurrentValue;
            var totalInvestment = portfolio.TotalInvestment;
            var totalPL = totalValue - totalInvestment;
            var plPercentage = totalInvestment > 0 ? (totalPL / totalInvestment) * 100 : 0;

            // Profitability warnings
            if (plPercentage < -30)
                warnings.Add($"🔴 Portfolio down {plPercentage:F1}% - Critical loss situation");
            else if (plPercentage < -20)
                warnings.Add($"🟡 Portfolio down {plPercentage:F1}% - Review all holdings");
            else if (plPercentage < -10)
                warnings.Add($"🟡 Portfolio down {plPercentage:F1}% - Consider stop-loss strategy");

            // Concentration warnings
            foreach (var holding in holdings)
            {
                var percentage = (holding.Quantity * holding.CurrentPrice / totalValue) * 100;
                if (percentage > 40)
                    warnings.Add($"🔴 {holding.Symbol} is {percentage:F1}% of portfolio - Extreme concentration risk");
                else if (percentage > 25)
                    warnings.Add($"🟡 {holding.Symbol} is {percentage:F1}% of portfolio - High concentration");
                else if (percentage > 15)
                    warnings.Add($"⚡ {holding.Symbol} is {percentage:F1}% of portfolio");
            }

            // Loss warnings
            var losers = holdings.Where(h => h.ProfitLoss < 0).ToList();
            if (losers.Any())
            {
                var worstLoser = losers.OrderBy(h => h.ProfitLossPercent).First();
                warnings.Add($"📉 Worst: {worstLoser.Symbol} ({worstLoser.ProfitLossPercent:F1}% loss, ₹{Math.Abs(worstLoser.ProfitLoss):N2})");

                var bigLosers = losers.Where(h => h.ProfitLossPercent < -20).ToList();
                if (bigLosers.Count > 1)
                    warnings.Add($"⚠️ {bigLosers.Count} stocks with >20% loss - Consider cutting losses");
            }

            // Sector warnings
            if (sectorExposure.Any())
            {
                var overExposed = sectorExposure.Where(s => s.Value > 40).ToList();
                foreach (var sector in overExposed)
                    warnings.Add($"🏭 {sector.Key} sector at {sector.Value:F1}% - High concentration");
            }

            return warnings.Distinct().Take(5).ToList();
        }

        private async Task<List<TradeItem>> GenerateBuyRecommendations(List<Holding> holdings, ITradingService tradingService, PortfolioSummary portfolio)
        {
            var recommendations = new List<TradeItem>();

            // === ENHANCEMENT 1: Better cash calculation ===
            // Calculate available cash (Current Value - Total Investment)
            // If negative, you have no cash available (or you're in loss)
            //var availableCash = portfolio.CurrentValue - portfolio.TotalInvestment;

            // Alternative: Use 20% of current value if you want to allow buys even when portfolio is down
            //var availableCash = portfolio.CurrentValue * 0.2m; // 20% of current value
            var availableCash = portfolio.CurrentValue * 0.2m; // 20% of current value for new investments

            _logger.LogDebug("Available cash for buys: ₹{Cash}", availableCash);

            // === ENHANCEMENT 2: Only show buy recommendations if you have positive cash ===
            if (availableCash <= 5000) // Minimum ₹5000 for a new position
            {
                _logger.LogDebug("Insufficient cash for new buys: ₹{Cash}", availableCash);
                return recommendations;
            }

            var existingSymbols = holdings.Select(h => h.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // === ENHANCEMENT 3: Get more trades and apply smarter filtering ===
            var topTrades = await tradingService.GetTopTradesAsync(15); // Get more candidates

            // Filter trades based on portfolio context
            var candidates = topTrades
                .Where(t => !existingSymbols.Contains(t.Symbol)) // Don't buy what you already own
                .Where(t => t.CurrentPrice <= availableCash) // Must be affordable
                .Where(t => t.Confidence >= 65) // Minimum confidence threshold
                .Where(t => t.RiskReward >= 1.2m) // Minimum risk/reward ratio
                .OrderByDescending(t => t.Confidence) // Highest confidence first
                .ThenByDescending(t => t.RiskReward) // Then best risk/reward
                .Take(3) // Top 3 recommendations
                .ToList();

            foreach (var trade in candidates)
            {
                // Calculate how many shares we can buy
                var maxShares = (int)(availableCash / trade.CurrentPrice);
                if (maxShares < 1) continue;

                // Adjust quantity based on portfolio size (don't put too much in one stock)
                var maxPositionValue = portfolio.CurrentValue * 0.05m; // Max 5% in one new position
                var recommendedShares = Math.Min(maxShares, (int)(maxPositionValue / trade.CurrentPrice));

                if (recommendedShares < 1) continue;

                // Create a copy of the trade with adjusted quantity
                var recommendation = new TradeItem
                {
                    Symbol = trade.Symbol,
                    Category = trade.Category,
                    CurrentPrice = trade.CurrentPrice,
                    TargetPrice = trade.TargetPrice,
                    StopLoss = trade.StopLoss,
                    Confidence = trade.Confidence,
                    RiskReward = trade.RiskReward,
                    PotentialReturn = trade.PotentialReturn,
                    Reason = trade.Reason,
                    Quantity = recommendedShares // Set recommended quantity
                };

                recommendations.Add(recommendation);
                availableCash -= recommendedShares * trade.CurrentPrice; // Reduce available cash
            }

            return recommendations;
        }
        private List<string> GenerateRebalanceSuggestions(List<Holding> holdings, PortfolioSummary portfolio, Dictionary<string, decimal> sectorExposure)
        {
            var suggestions = new List<string>();

            if (holdings == null || !holdings.Any())
                return suggestions;

            var totalValue = portfolio.CurrentValue;

            // Rebalance over-concentrated positions
            foreach (var holding in holdings)
            {
                var percentage = (holding.Quantity * holding.CurrentPrice / totalValue) * 100;
                if (percentage > 25)
                {
                    var targetPercentage = 15m;
                    var targetValue = totalValue * (targetPercentage / 100);
                    var currentValue = holding.Quantity * holding.CurrentPrice;
                    var excessValue = currentValue - targetValue;
                    var sharesToSell = (int)(excessValue / holding.CurrentPrice);

                    if (sharesToSell > 0)
                    {
                        suggestions.Add($"🔴 Sell {sharesToSell} {holding.Symbol} to reduce from {percentage:F1}% to {targetPercentage:F1}%");
                    }
                }
            }

            // Rebalance under-performing positions
            var losers = holdings.Where(h => h.ProfitLossPercent < -15).ToList();
            foreach (var loser in losers)
            {
                suggestions.Add($"📉 Review {loser.Symbol} - Down {loser.ProfitLossPercent:F1}%");
            }

            // Sector rebalancing
            foreach (var sector in sectorExposure)
            {
                if (sector.Value > 40)
                {
                    suggestions.Add($"🏭 Reduce {sector.Key} exposure from {sector.Value:F1}% to below 30%");
                }
                else if (sector.Value < 5 && sectorExposure.Count > 3)
                {
                    suggestions.Add($"💡 Consider increasing {sector.Key} exposure for better diversification");
                }
            }

            // Portfolio size suggestions
            if (holdings.Count < 5)
            {
                var additionalNeeded = 8 - holdings.Count;
                suggestions.Add($"📊 Add {additionalNeeded} more stocks for better diversification (target 8-12 holdings)");
            }
            else if (holdings.Count > 15)
            {
                var toReduce = holdings.Count - 12;
                suggestions.Add($"📊 Consider consolidating - remove {toReduce} underperforming stocks");
            }

            return suggestions.Take(3).ToList();
        }

        private Dictionary<string, decimal> CalculateSectorAnalysis(List<Holding> holdings)
        {
            var sectorAllocation = new Dictionary<string, decimal>();
            var totalValue = holdings.Sum(h => h.Quantity * h.CurrentPrice);

            foreach (var holding in holdings)
            {
                string sector = GetSectorFromSymbol(holding.Symbol);
                var value = holding.Quantity * holding.CurrentPrice;

                if (sectorAllocation.ContainsKey(sector))
                    sectorAllocation[sector] += value;
                else
                    sectorAllocation[sector] = value;
            }

            // Convert to percentages
            var result = new Dictionary<string, decimal>();
            foreach (var kv in sectorAllocation)
            {
                result[kv.Key] = totalValue > 0 ? (kv.Value / totalValue) * 100 : 0;
            }

            return result;
        }

        private string GetSectorFromSymbol(string symbol)
        {
            var upperSymbol = symbol.ToUpper().Replace("-EQ", "").Replace(".NS", "").Replace(".BO", "").Trim();

            return upperSymbol switch
            {
                // Energy & Oil & Gas
                "RELIANCE" => "Energy",
                "ONGC" => "Energy",
                "OIL" => "Energy",
                "IOC" => "Energy",
                "BPCL" => "Energy",
                "HPCL" => "Energy",
                "GAIL" => "Energy",
                "GUJGASLTD" => "Energy",
                "MGL" => "Energy",
                "IGL" => "Energy",
                "PETRONET" => "Energy",
                "ADANIGREEN" => "Energy",

                // Power & Utilities
                "NTPC" => "Power",
                "POWERGRID" => "Power",
                "TATAPOWER" => "Power",
                "ADANIPOWER" => "Power",
                "TORNTPOWER" => "Power",
                "JSWENERGY" => "Power",
                "NHPC" => "Power",
                "SJVN" => "Power",
                "CESC" => "Power",
                "RPOWER" => "Power",

                // Engineering & Capital Goods
                "ARE&M" => "Engineering",
                "LT" => "Engineering",
                "SIEMENS" => "Engineering",
                "ABB" => "Engineering",
                "BHEL" => "Engineering",
                "BEL" => "Engineering",
                "HAL" => "Engineering",
                "CUMMINSIND" => "Engineering",
                "THERMAX" => "Engineering",
                "PRAJIND" => "Engineering",
                "OLECTRA" => "Engineering",

                // IT & Technology
                "TCS" => "Technology",
                "INFY" => "Technology",
                "HCLTECH" => "Technology",
                "TECHM" => "Technology",
                "WIPRO" => "Technology",
                "LTTS" => "Technology",
                "PERSISTENT" => "Technology",
                "MPHASIS" => "Technology",
                "MINDTREE" => "Technology",
                "COFORGE" => "Technology",
                "OFSS" => "Technology",
                "BIRLACORPN" => "Technology",
                "ZENSARTECH" => "Technology",
                "KPITTECH" => "Technology",

                // Banking
                "HDFCBANK" => "Banking",
                "ICICIBANK" => "Banking",
                "SBIN" => "Banking",
                "KOTAKBANK" => "Banking",
                "AXISBANK" => "Banking",
                "INDUSINDBK" => "Banking",
                "FEDERALBNK" => "Banking",
                "IDFCFIRSTB" => "Banking",
                "BANDHANBNK" => "Banking",
                "PNB" => "Banking",
                "BANKBARODA" => "Banking",
                "CANBK" => "Banking",
                "YESBANK" => "Banking",
                "RBLBANK" => "Banking",
                "AUBANK" => "Banking",

                // Finance & NBFC
                "BAJFINANCE" => "Finance",
                "BAJAJFINSV" => "Finance",
                "HDFC" => "Finance",
                "HDFCAMC" => "Finance",
                "ICICIPRULI" => "Finance",
                "ICICIGI" => "Finance",
                "SBILIFE" => "Insurance",
                "HDFCLIFE" => "Insurance",
                "LICI" => "Insurance",
                "MUTHOOTFIN" => "Finance",
                "IIFL" => "Finance",
                "PNBHOUSING" => "Finance",
                "L&TFH" => "Finance",
                "CHOLAFIN" => "Finance",

                // FMCG
                "ITC" => "FMCG",
                "HINDUNILVR" => "FMCG",
                "NESTLE" => "FMCG",
                "BRITANNIA" => "FMCG",
                "DABUR" => "FMCG",
                "MARICO" => "FMCG",
                "TATACONSUM" => "FMCG",
                "GODREJCP" => "FMCG",
                "COLPAL" => "FMCG",
                "VBL" => "FMCG",
                "RADICO" => "FMCG",
                "UBL" => "FMCG",
                "MCDOWELL-N" => "FMCG",

                // Automobile
                "TATAMOTORS" => "Automobile",
                "MARUTI" => "Automobile",
                "M&M" => "Automobile",
                "BAJAJ-AUTO" => "Automobile",
                "EICHERMOT" => "Automobile",
                "HEROMOTOCO" => "Automobile",
                "TATAELXSI" => "Automobile",
                "TMPV" => "Automobile",
                "ASHOKLEY" => "Automobile",
                "TVSMOTOR" => "Automobile",
                "BALKRISIND" => "Auto Ancillary",
                "MOTHERSUMI" => "Auto Ancillary",
                "ENDURANCE" => "Auto Ancillary",
                "EXIDEIND" => "Auto Ancillary",
                "AMARAJABAT" => "Auto Ancillary",
                "BOSCHLTD" => "Auto Ancillary",

                // Pharma & Healthcare
                "SUNPHARMA" => "Pharma",
                "DRREDDY" => "Pharma",
                "CIPLA" => "Pharma",
                "DIVISLAB" => "Pharma",
                "BIOCON" => "Pharma",
                "LUPIN" => "Pharma",
                "ALKEM" => "Pharma",
                "TORNTPHARM" => "Pharma",
                "AUROPHARMA" => "Pharma",
                "CADILAHC" => "Pharma",
                "GLENMARK" => "Pharma",
                "APOLLOHOSP" => "Healthcare",
                "FORTIS" => "Healthcare",
                "MAXHEALTH" => "Healthcare",

                // Construction & Cement
                "ULTRACEMCO" => "Cement",
                "GRASIM" => "Cement",
                "AMBUJACEM" => "Cement",
                "ACC" => "Cement",
                "SHREECEM" => "Cement",
                "RAMCOCEM" => "Cement",
                "DALBHARAT" => "Cement",
                "ADANIPORTS" => "Infrastructure",
                "NAVINFLUOR" => "Construction",

                // Metals & Mining
                "TATASTEEL" => "Metals",
                "HINDALCO" => "Metals",
                "JSWSTEEL" => "Metals",
                "SAIL" => "Metals",
                "NATIONALUM" => "Metals",
                "HINDZINC" => "Metals",
                "NMDC" => "Metals",
                "COALINDIA" => "Mining",
                "VEDL" => "Metals",
                "JINDALSTEL" => "Metals",

                // Telecom & Media
                "BHARTIARTL" => "Telecom",
                "IDEA" => "Telecom",
                "INDUSTOWER" => "Telecom",
                "ZEEL" => "Media",
                "PVR" => "Media",
                "NETWORK18" => "Media",
                "TV18BRDCST" => "Media",

                // Chemicals
                "PIDILITIND" => "Chemicals",
                "SRTRANSFIN" => "Chemicals",
                "UPL" => "Agro Chemicals",
                "PIIND" => "Agro Chemicals",
                "BASF" => "Chemicals",
                "SOLARINDS" => "Chemicals",
                "FLUOROCHEM" => "Chemicals",

                // Consumer Durables & Retail
                "TITAN" => "Consumer Durables",
                "VOLTAS" => "Consumer Durables",
                "HAVELLS" => "Consumer Durables",
                "CROMPTON" => "Consumer Durables",
                "WHIRLPOOL" => "Consumer Durables",
                "BLUESTARCO" => "Consumer Durables",
                "AMBER" => "Consumer Durables",
                "TRENT" => "Retail",
                "DMART" => "Retail",

                // Textiles & Apparel
                "PAGEIND" => "Textiles",
                "KPRMILL" => "Textiles",

                // Real Estate
                "DLF" => "Real Estate",
                "GODREJPROP" => "Real Estate",
                "OBEROIRLTY" => "Real Estate",
                "BRIGADE" => "Real Estate",
                "SOBHA" => "Real Estate",

                // Logistics & Shipping
                "CONCOR" => "Logistics",
                "GSPL" => "Logistics",
                "BLUEDART" => "Logistics",
                "TCI" => "Logistics",
                "MAHLOG" => "Logistics",
                "SCI" => "Shipping",
                "GEESHIPS" => "Shipping",

                _ => "Other"
            };
        }

        // ========== ALERT COMMANDS ==========

        private async Task HandleAlertCommand(long chatId, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 3)
            {
                await SendMessageAsync(chatId,
                    "❌ Invalid format. Use:\n" +
                    "/alert SYMBOL PRICE [above/below]\n" +
                    "Example: /alert RELIANCE 2500 above");
                return;
            }

            var symbol = parts[1].ToUpper();

            if (!decimal.TryParse(parts[2], out decimal targetPrice))
            {
                await SendMessageAsync(chatId, "❌ Invalid price. Please enter a valid number.");
                return;
            }

            bool isAbove = parts.Length < 4 || parts[3].ToLower() != "below";

            try
            {
                await _priceAlertService.AddAlertAsync(chatId, symbol, targetPrice, isAbove);

                var direction = isAbove ? "above" : "below";
                await SendMessageAsync(chatId,
                    $"✅ Alert set for {symbol} at ₹{targetPrice:N2} {direction}\n\n" +
                    $"You'll be notified when price goes {direction} this level.");
            }
            catch (ArgumentException ex)
            {
                await SendMessageAsync(chatId, $"❌ {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting alert for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error setting alert. Please try again.");
            }
        }

        private async Task HandleRemoveAlertCommand(long chatId, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                await SendMessageAsync(chatId, "❌ Please provide alert ID. Example: /removealert 123");
                return;
            }

            if (!int.TryParse(parts[1], out int alertId))
            {
                await SendMessageAsync(chatId, "❌ Invalid alert ID.");
                return;
            }

            try
            {
                var removed = await _priceAlertService.RemoveAlertAsync(chatId, alertId);
                if (removed)
                {
                    await SendMessageAsync(chatId, $"✅ Alert #{alertId} removed successfully.");
                }
                else
                {
                    await SendMessageAsync(chatId, $"❌ Alert #{alertId} not found.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing alert for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error removing alert. Please try again.");
            }
        }

        private async Task ShowAlerts(long chatId)
        {
            try
            {
                var alerts = await _priceAlertService.GetUserAlertsAsync(chatId);

                if (!alerts.Any())
                {
                    await SendMessageAsync(chatId,
                        "📋 You have no alerts.\n\n" +
                        "Set one with: /alert SYMBOL PRICE above/below");
                    return;
                }

                var activeAlerts = alerts.Where(a => !a.IsTriggered).ToList();
                var triggeredAlerts = alerts.Where(a => a.IsTriggered).ToList();

                var sb = new StringBuilder();
                sb.AppendLine("<b>📋 Your Price Alerts</b>\n");

                if (activeAlerts.Any())
                {
                    sb.AppendLine("<b>🟢 Active Alerts:</b>");
                    foreach (var alert in activeAlerts.OrderBy(a => a.Symbol))
                    {
                        var direction = alert.IsAbove ? "⬆️ above" : "⬇️ below";
                        sb.AppendLine($"🆔 {alert.Id}: {alert.Symbol} {direction} ₹{alert.TargetPrice:N2}");
                        sb.AppendLine($"   Set: {alert.CreatedAt:dd MMM HH:mm}");
                    }
                    sb.AppendLine();
                }

                if (triggeredAlerts.Any())
                {
                    sb.AppendLine("<b>✅ Triggered Alerts:</b>");
                    foreach (var alert in triggeredAlerts.OrderByDescending(a => a.TriggeredAt).Take(5))
                    {
                        var direction = alert.IsAbove ? "above" : "below";
                        sb.AppendLine($"   {alert.Symbol} moved {direction} ₹{alert.TargetPrice:N2}");
                        sb.AppendLine($"   Triggered at ₹{alert.TriggeredPrice:N2} on {alert.TriggeredAt:dd MMM HH:mm}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("<b>Commands:</b>");
                sb.AppendLine("/alert SYMBOL PRICE above - Set alert");
                sb.AppendLine("/alerts - Show all alerts");
                sb.AppendLine("/clearalerts - Clear triggered alerts");
                sb.AppendLine("/removealert ID - Remove specific alert");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error showing alerts for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching alerts.");
            }
        }

        private async Task ClearAlerts(long chatId)
        {
            try
            {
                await _priceAlertService.ClearTriggeredAlertsAsync(chatId);
                await SendMessageAsync(chatId, "✅ All triggered alerts cleared.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing alerts for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error clearing alerts.");
            }
        }

        private async Task SendMarketStatus(long chatId)
        {
            using var scope = _scopeFactory.CreateScope();
            var marketStatus = scope.ServiceProvider.GetRequiredService<IMarketStatusService>();

            var message = marketStatus.GetMarketStatusMessage();
            await SendMessageAsync(chatId, message);
        }

        private async Task HandlePriceCommand(long chatId, string text)
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

                await SendMessageAsync(chatId, "❓ Would you like to execute these optimizations? Use /execute_trades to proceed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing holdings for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error optimizing holdings. Please try again later.");
            }
        }

        private async Task ExecuteTodaysTrades(long chatId)
        {
            await SendMessageAsync(chatId, "🔄 Analyzing and executing today's trades...");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var autoTrading = scope.ServiceProvider.GetRequiredService<AutoTradingService>();
                var optimizer = scope.ServiceProvider.GetRequiredService<HoldingOptimizer>();

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
• 🔔 Price alerts

<b>Commands:</b>
/help - Show all commands
/stocks - View top 10 stocks
/topgainers - Today's top gainers
/toplosers - Today's top losers
/price RELIANCE - Get stock price
/alert RELIANCE 2500 above - Set price alert
/alerts - View your alerts
/subscribe - Get daily updates
/portfolio_status - View your portfolio
/suggest - Get portfolio improvement suggestions

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
/market - Check if market is open

<b>🔔 Price Alerts:</b>
/alert SYMBOL PRICE above/below - Set price alert
/alerts - View all your alerts
/clearalerts - Clear triggered alerts
/removealert ID - Remove specific alert

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
/suggest - Get detailed portfolio improvement plan
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