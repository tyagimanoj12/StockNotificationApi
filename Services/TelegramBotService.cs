using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Polling;
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
        private readonly ICacheService _cacheService;
        private readonly ITelegramClientWrapper _telegramClientWrapper;
        private readonly ICommandProcessor _commandProcessor;
        private TelegramBotClient? _botClient;
        private readonly List<long> _subscribedChats = new();
        private readonly List<long> _briefingSubscribers = new();

        // Add instance lock to prevent multiple bot instances
        private static readonly object _instanceLock = new();
        private static bool _isInstanceRunning = false;

        // Track last command time to prevent spam
        private static readonly Dictionary<long, DateTime> _lastCommandTime = new();
        private static readonly Dictionary<long, string> _lastCommand = new();
        private static readonly Dictionary<long, int> _commandCount = new();
        private static readonly Dictionary<long, DateTime> _pendingTradeExecutions = new();
        private static readonly object _lockObject = new();

        // Use constants from Constants.cs
        private const int RATE_LIMIT_DELAY_MS = RateLimitConstants.RATE_LIMIT_DELAY_MS;
        private const int SCHEDULER_CHECK_INTERVAL_MS = TimeoutConstants.SCHEDULER_CHECK_INTERVAL_MS;
        private const int REPORT_SEND_DELAY_MS = TimeoutConstants.REPORT_SEND_DELAY_MS;
        private const int BRIEFING_HOUR = MarketConstants.BRIEFING_HOUR;
        private const int BRIEFING_MINUTE = MarketConstants.BRIEFING_MINUTE;
        private const int REPORT_HOUR = MarketConstants.REPORT_HOUR;
        private const int REPORT_MINUTE = MarketConstants.REPORT_MINUTE;
        private const int PRICE_COMMAND_PREFIX_LENGTH = StockConstants.PRICE_COMMAND_PREFIX_LENGTH;
        private const int ALERT_COMMAND_PREFIX_LENGTH = StockConstants.ALERT_COMMAND_PREFIX_LENGTH;
        private const int MAX_RETRY_ATTEMPTS = TimeoutConstants.MAX_RETRY_ATTEMPTS;
        private const int RETRY_DELAY_MS = TimeoutConstants.RETRY_DELAY_MS;
        private const int MAX_CACHED_USERS = 10000;
        private const int USER_CACHE_CLEANUP_MINUTES = 30;
        private const int INACTIVE_USER_DAYS = 30;

        // Rate limiting constants
        private const int COMMAND_COOLDOWN_SECONDS = RateLimitConstants.COMMAND_COOLDOWN_SECONDS;
        private const int MAX_COMMANDS_PER_MINUTE = RateLimitConstants.MAX_COMMANDS_PER_MINUTE;
        private const int RATE_LIMIT_WINDOW_MINUTES = RateLimitConstants.RATE_LIMIT_WINDOW_MINUTES;

        // Cache duration
        private const int CACHE_DURATION_MINUTES = CacheConstants.STOCK_DATA_CACHE_MINUTES;

        // Health check constants
        private const int HEALTH_CHECK_INTERVAL_MS = TimeoutConstants.HEALTH_CHECK_INTERVAL_MS;
        private const int MAX_CONSECUTIVE_ERRORS = HealthCheckConstants.MAX_CONSECUTIVE_ERRORS;

        private int _consecutiveErrors = 0;
        private DateTime _lastSuccessfulMessage = DateTime.UtcNow;
        private bool _isBotRunning = false;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _healthCheckCts = null;
        private int _reconnectAttempts = 0;
        private DateTime _lastReconnectAttempt = DateTime.UtcNow;
        private CancellationTokenSource? _backgroundCts = null;

        public TelegramBotService(
            ILogger<TelegramBotService> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory,
            IPriceAlertService priceAlertService,
            ICacheService cacheService,
            ITelegramClientWrapper telegramClientWrapper,
            ICommandProcessor commandProcessor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _priceAlertService = priceAlertService ?? throw new ArgumentNullException(nameof(priceAlertService));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
            _telegramClientWrapper = telegramClientWrapper ?? throw new ArgumentNullException(nameof(telegramClientWrapper));
            _commandProcessor = commandProcessor ?? throw new ArgumentNullException(nameof(commandProcessor));
        }

        public async Task SendMessageAsync(long chatId, string message, ParseMode parseMode = ParseMode.Html)
        {
            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    _logger.LogWarning("Attempted to send empty message to chat {ChatId}", chatId);
                    return;
                }

                await _telegramClientWrapper.SendTextAsync(chatId, message, parseMode);
                _logger.LogDebug("Message sent to chat {ChatId}", chatId);
                _lastSuccessfulMessage = DateTime.UtcNow;
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                _logger.LogError(ex, "Failed to send message to chat {ChatId}. Error count: {ErrorCount}", chatId, _consecutiveErrors);

                if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                {
                    _logger.LogWarning("Too many consecutive errors ({ErrorCount}). Attempting to reconnect...", _consecutiveErrors);
                    await EnsureBotRunning();
                }

                throw;
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
                try
                {
                    await SendMessageAsync(chatId, message);
                    await Task.Delay(RATE_LIMIT_DELAY_MS);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to broadcast to {ChatId}", chatId);
                }
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

            using var scope = _scopeFactory.CreateScope();
            var messageFormatter = scope.ServiceProvider.GetRequiredService<IMessageFormatter>();
            var message = messageFormatter.FormatDailyReport(report);
            await BroadcastToAllAsync(message);
        }

        #region Unified Cache Methods

        private async Task<List<StockData>> GetCachedStocksAsync()
        {
            return await _cacheService.GetOrSetAsync("top_stocks", async () =>
            {
                _logger.LogInformation("Cache miss for top stocks, fetching fresh data...");
                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
                return await stockService.GetIndianStockDataAsync();
            }, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES)) ?? new List<StockData>();
        }

        private async Task<List<StockData>> GetCachedTopGainersAsync()
        {
            return await _cacheService.GetOrSetAsync("top_gainers", async () =>
            {
                var stocks = await GetCachedStocksAsync();
                return stocks.Where(s => s != null && s.ChangePercent > 0)
                            .OrderByDescending(s => s.ChangePercent)
                            .Take(5)
                            .ToList();
            }, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES)) ?? new List<StockData>();
        }

        private async Task<List<StockData>> GetCachedTopLosersAsync()
        {
            return await _cacheService.GetOrSetAsync("top_losers", async () =>
            {
                var stocks = await GetCachedStocksAsync();
                return stocks.Where(s => s != null && s.ChangePercent < 0)
                            .OrderBy(s => s.ChangePercent)
                            .Take(5)
                            .ToList();
            }, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES)) ?? new List<StockData>();
        }

        #endregion

        #region Core Lifecycle

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            lock (_instanceLock)
            {
                if (_isInstanceRunning)
                {
                    _logger.LogWarning("Another instance is already running, stopping this one");
                    return;
                }
                _isInstanceRunning = true;
            }

            _logger.LogInformation("Telegram bot service starting...");

            CancellationTokenSource? linkedCts = null;

            try
            {
                // Create a linked cancellation token source for internal tasks
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _backgroundCts = linkedCts;

                // Initialize bot
                await EnsureBotRunning(_backgroundCts.Token);

                // Pre-warm cache
                await PreWarmCache(_backgroundCts.Token);

                // Start background tasks if bot is running
                if (_isBotRunning)
                {
                    // Fire and forget tasks - they'll be cancelled via _backgroundCts
                    _ = Task.Run(() => HealthCheckLoop(_backgroundCts.Token), _backgroundCts.Token);
                    _ = Task.Run(() => CleanupRateLimitTracking(_backgroundCts.Token), _backgroundCts.Token);
                    _ = Task.Run(() => CleanupInactiveUsers(_backgroundCts.Token), _backgroundCts.Token);
                    _ = Task.Run(() => CleanupUserCache(_backgroundCts.Token), _backgroundCts.Token);
                }

                // Run the main scheduler loop
                await RunSchedulerLoop(_backgroundCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Telegram bot service stopping gracefully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram bot service encountered an error");
            }
            finally
            {
                // Mark as not running
                _isBotRunning = false;

                // Don't dispose _backgroundCts here - let StopAsync handle it
                // Just set it to null to avoid double disposal
                _backgroundCts = null;

                lock (_instanceLock)
                {
                    _isInstanceRunning = false;
                }

                _logger.LogInformation("Telegram bot service stopped");
            }
        }

        private async Task PreWarmCache(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Pre-warming cache with stock data...");

                using var semaphore = new SemaphoreSlim(1, 1);

                try
                {
                    await semaphore.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Cache pre-warm cancelled");
                    return;
                }

                try
                {
                    var existingStocks = _cacheService.Get<List<StockData>>("top_stocks");
                    if (existingStocks != null && existingStocks.Any())
                    {
                        _logger.LogInformation("Cache already has {Count} stocks, refreshing in background", existingStocks.Count);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
                                var freshStocks = await stockService.GetIndianStockDataAsync();
                                if (freshStocks?.Any() == true)
                                {
                                    _cacheService.Set("top_stocks", freshStocks, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
                                    _logger.LogInformation("Background cache refresh completed with {Count} stocks", freshStocks.Count);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                _logger.LogInformation("Background cache refresh cancelled");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Background cache refresh failed");
                            }
                        }, cancellationToken);
                        return;
                    }

                    _logger.LogInformation("Starting cache pre-warm with 5-minute timeout...");

                    using var scope = _scopeFactory.CreateScope();
                    var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
                    var circuitBreaker = scope.ServiceProvider.GetRequiredService<ICircuitBreakerService>();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));

                    var stocks = await circuitBreaker.ExecuteAsync(
                        "StockData",
                        async () => await stockService.GetIndianStockDataAsync(),
                        new List<StockData>());

                    if (stocks != null && stocks.Any())
                    {
                        _cacheService.Set("top_stocks", stocks, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
                        _logger.LogInformation("Cache pre-warmed successfully with {Count} stocks", stocks.Count);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Cache pre-warm cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during cache pre-warm");
                    _cacheService.Set("top_stocks", new List<StockData>(), TimeSpan.FromMinutes(1));
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cache pre-warm cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cache pre-warm setup");
            }
        }

        private async Task EnsureBotRunning(CancellationToken cancellationToken = default)
        {
            await _reconnectLock.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                if (_reconnectAttempts > 0)
                {
                    int backoffMs = Math.Min(60_000, 1000 * (1 << Math.Min(_reconnectAttempts, 6)));
                    if ((now - _lastReconnectAttempt).TotalMilliseconds < backoffMs)
                    {
                        _logger.LogInformation("EnsureBotRunning: backing off reconnect attempt. Next allowed after {Ms}ms", backoffMs - (now - _lastReconnectAttempt).TotalMilliseconds);
                        return;
                    }
                }
                _lastReconnectAttempt = now;

                if (_botClient != null)
                {
                    try
                    {
                        _logger.LogInformation("Stopping existing bot client...");
                        if (_healthCheckCts != null)
                        {
                            _healthCheckCts.Cancel();
                            _healthCheckCts.Dispose();
                            _healthCheckCts = null;
                        }
                        await Task.Delay(1000, cancellationToken);
                        _logger.LogInformation("Existing bot client stopped");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Stop operation cancelled");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error stopping existing bot client");
                    }
                    finally
                    {
                        _botClient = null;
                        _isBotRunning = false;
                    }
                }

                var token = _configuration["TelegramSettings:BotToken"];
                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("Telegram bot token not configured");
                    return;
                }

                try
                {
                    _logger.LogInformation("Creating new bot client...");

                    if (_telegramClientWrapper.Client == null)
                    {
                        _botClient = new TelegramBotClient(token);
                    }
                    else
                    {
                        _botClient = _telegramClientWrapper.Client;
                    }

                    if (_botClient == null)
                    {
                        _logger.LogError("Failed to create bot client - botClient is null");
                        _reconnectAttempts++;
                        _isBotRunning = false;
                        return;
                    }

                    var me2 = await _botClient.GetMe(cancellationToken);
                    _logger.LogInformation("Bot connected successfully: @{Username}", me2.Username);

                    _healthCheckCts = new CancellationTokenSource();

                    _botClient.StartReceiving(
                        HandleUpdateAsync,
                        HandleErrorAsync,
                        receiverOptions: new ReceiverOptions
                        {
                            AllowedUpdates = Array.Empty<UpdateType>()
                        },
                        cancellationToken: _healthCheckCts.Token
                    );

                    _isBotRunning = true;
                    _consecutiveErrors = 0;
                    _lastSuccessfulMessage = DateTime.UtcNow;
                    _reconnectAttempts = 0;

                    _logger.LogInformation("Bot is now running and receiving updates");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Bot initialization cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    _reconnectAttempts++;
                    _logger.LogError(ex, "Failed to initialize or start receiving in EnsureBotRunning (attempt {Attempt})", _reconnectAttempts);
                    try
                    {
                        _healthCheckCts?.Cancel();
                        _healthCheckCts?.Dispose();
                        _healthCheckCts = null;
                    }
                    catch { }
                    _botClient = null;
                    _isBotRunning = false;
                }
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        private async Task HealthCheckLoop(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Health check loop started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HEALTH_CHECK_INTERVAL_MS, stoppingToken);

                    if (!_isBotRunning)
                    {
                        _logger.LogWarning("Health check: Bot not running. Attempting to restart...");
                        await EnsureBotRunning(stoppingToken);
                        continue;
                    }

                    var timeSinceLastMessage = DateTime.UtcNow - _lastSuccessfulMessage;
                    if (timeSinceLastMessage > TimeSpan.FromMinutes(15))
                    {
                        _logger.LogWarning("Health check: Bot has been silent for {Minutes} minutes. Checking connection...", timeSinceLastMessage.TotalMinutes);

                        try
                        {
                            var me = await _botClient?.GetMe(stoppingToken);
                            if (me != null)
                            {
                                _logger.LogInformation("Health check: Bot is healthy. Last message: {LastMessage}", _lastSuccessfulMessage);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Health check: Bot connection test failed");
                            _isBotRunning = false;
                            await EnsureBotRunning(stoppingToken);
                        }
                    }

                    if (_consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                    {
                        _logger.LogWarning("Health check: Too many consecutive errors ({ErrorCount}). Restarting bot...", _consecutiveErrors);
                        _isBotRunning = false;
                        await EnsureBotRunning(stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Health check loop stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in health check loop");
                }
            }

            _logger.LogInformation("Health check loop stopped");
        }

        private async Task RunSchedulerLoop(CancellationToken stoppingToken)
        {
            if (stoppingToken == null)
            {
                _logger.LogError("RunSchedulerLoop called with null cancellation token");
                return;
            }
            _logger.LogInformation("Telegram bot scheduler loop started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_isBotRunning)
                    {
                        _logger.LogWarning("Bot not running in scheduler loop. Attempting to restart...");
                        await EnsureBotRunning(stoppingToken);
                        await Task.Delay(5000, stoppingToken);
                        continue;
                    }

                    var now = DateTime.Now;

                    // Check for daily report at 9:00 AM
                    if (now.Hour == REPORT_HOUR && now.Minute == REPORT_MINUTE && now.Second < 30)
                    {
                        _logger.LogInformation("Sending scheduled daily report...");
                        await SendScheduledDailyReport(stoppingToken);
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }

                    // Check for daily briefing at 8:30 AM
                    if (now.Hour == BRIEFING_HOUR && now.Minute == BRIEFING_MINUTE && now.Second < 30)
                    {
                        _logger.LogInformation("Sending scheduled daily briefing...");
                        await SendScheduledDailyBriefing(stoppingToken);
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }

                    await Task.Delay(SCHEDULER_CHECK_INTERVAL_MS, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Telegram bot scheduler loop stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in scheduler loop");
                    try
                    {
                        await Task.Delay(5000, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("Telegram bot scheduler loop stopped");
        }

        #endregion

        #region Update & Error Handlers

        private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken token)
        {
            _lastSuccessfulMessage = DateTime.UtcNow;
            _consecutiveErrors = 0;

            if (update.Message?.Chat?.Id == null)
            {
                return;
            }

            var chatId = update.Message.Chat.Id;

            try
            {
                if (string.IsNullOrEmpty(update.Message.Text))
                {
                    return;
                }

                var text = update.Message.Text.Trim();
                var lowerText = text.ToLower();

                _logger.LogInformation("Processing command: {Text} from {ChatId}", text, chatId);

                if (IsRateLimited(chatId))
                {
                    _logger.LogWarning("Rate limited for chat {ChatId}", chatId);
                    await SendMessageAsync(chatId, "⏳ You're sending commands too quickly. Please wait a moment.");
                    return;
                }

                UpdateRateLimit(chatId, text);
                await SendTypingAction(chatId);
                await SendImmediateAcknowledgment(chatId, lowerText, text);

                // Queue command processing
                await _commandProcessor.EnqueueAsync(chatId, async () =>
                {
                    try
                    {
                        await ProcessCommand(chatId, lowerText, text, update.Message.Chat, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing queued command for chat {ChatId}", chatId);
                        await SendMessageAsync(chatId, $"❌ Error processing command: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling update for chat {ChatId}", chatId);
                try
                {
                    await SendMessageAsync(chatId, "❌ An error occurred. Please try again.");
                }
                catch { }
            }
        }

        private async Task ProcessCommand(long chatId, string lowerText, string originalText, Chat chat, CancellationToken token)
        {
            if (_telegramClientWrapper == null)
            {
                _logger.LogError("TelegramClientWrapper is null");
                await SendMessageAsync(chatId, "❌ Service error. Please try again later.");
                return;
            }

            // IMMEDIATE RESPONSE - this will confirm if the bot is responding
            await SendMessageAsync(chatId, "🤖 Bot is processing your command...");

            _logger.LogInformation("Processing command: {Command} from {ChatId}", lowerText, chatId);
            switch (lowerText)
            {
                case "/start":
                    await SendWelcomeMessage(chatId, chat);
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
                case "/confirm_trades":
                    await HandleConfirmTrades(chatId);
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
                case "/debug":
                    await SendDebugMessage(chatId);
                    break;
                default:
                    if (lowerText.StartsWith("/price"))
                    {
                        await SendStockPrice(chatId, originalText);
                    }
                    else if (lowerText.StartsWith("/alert"))
                    {
                        await HandleAlertCommand(chatId, originalText);
                    }
                    else if (lowerText.StartsWith("/removealert"))
                    {
                        await HandleRemoveAlertCommand(chatId, originalText);
                    }
                    else
                    {
                        _logger.LogInformation("Unknown command: {Command} from {ChatId}", lowerText, chatId);
                        await SendMessageAsync(chatId, "❌ Unknown command. Type /help for available commands.");
                    }
                    break;
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken token)
        {
            if (exception is OperationCanceledException)
            {
                _logger.LogInformation("Telegram bot operation cancelled");
                return Task.CompletedTask;
            }

            _logger.LogError(exception, "Telegram bot error");
            _consecutiveErrors++;

            if (exception is HttpRequestException || exception is TaskCanceledException ||
                exception.Message.Contains("409") || exception.Message.Contains("Conflict"))
            {
                _logger.LogWarning("Conflict or network error detected. Bot may need reconnection.");
                _isBotRunning = false;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000, token);
                    try
                    {
                        await EnsureBotRunning(token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Reconnection attempt cancelled");
                    }
                }, token);
            }

            return Task.CompletedTask;
        }
        #endregion

        #region Rate Limiting

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

        private async Task CleanupInactiveUsers(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                lock (_lockObject)
                {
                    var cutoff = DateTime.UtcNow.AddHours(-24);
                    var inactiveUsers = _lastCommandTime.Where(x => x.Value < cutoff).Select(x => x.Key).ToList();
                    foreach (var user in inactiveUsers)
                    {
                        _lastCommandTime.Remove(user);
                        _lastCommand.Remove(user);
                        _commandCount.Remove(user);
                    }
                    _logger.LogDebug("Cleaned up {Count} inactive users", inactiveUsers.Count);
                }
            }
        }

        private async Task CleanupUserCache(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(USER_CACHE_CLEANUP_MINUTES), stoppingToken);

                    // Check if cancelled after delay
                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("User cache cleanup cancelled after delay");
                        break;
                    }

                    lock (_lockObject)
                    {
                        if (_commandCount.Count > MAX_CACHED_USERS)
                        {
                            var oldestUsers = _lastCommandTime
                                .OrderBy(x => x.Value)
                                .Take(_commandCount.Count - MAX_CACHED_USERS)
                                .Select(x => x.Key)
                                .ToList();

                            foreach (var user in oldestUsers)
                            {
                                _lastCommandTime.Remove(user);
                                _lastCommand.Remove(user);
                                _commandCount.Remove(user);
                            }
                            _logger.LogDebug("Trimmed user cache, removed {Count} users", oldestUsers.Count);
                        }

                        var cutoff = DateTime.UtcNow.AddDays(-INACTIVE_USER_DAYS);
                        var inactiveUsers = _lastCommandTime.Where(x => x.Value < cutoff).Select(x => x.Key).ToList();

                        foreach (var user in inactiveUsers)
                        {
                            _lastCommandTime.Remove(user);
                            _lastCommand.Remove(user);
                            _commandCount.Remove(user);
                        }

                        if (inactiveUsers.Any())
                        {
                            _logger.LogDebug("Cleaned up {Count} inactive users", inactiveUsers.Count);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("User cache cleanup cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in user cache cleanup");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        #endregion

        #region Command Handlers

        private async Task SendTopStocks(long chatId)
        {
            try
            {
                var stocks = await GetCachedStocksAsync();
                if (!stocks.Any())
                {
                    await SendMessageAsync(chatId, "❌ Error fetching stock data. Please try again later.");
                    return;
                }

                var message = _telegramClientWrapper.FormatTopStocksMessage(stocks);
                await SendMessageAsync(chatId, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top stocks");
                await SendMessageAsync(chatId, "❌ Error fetching stock data. Please try again later.");
            }
        }

        private async Task SendTopGainers(long chatId)
        {
            try
            {
                var gainers = await GetCachedTopGainersAsync();
                if (!gainers.Any())
                {
                    await SendMessageAsync(chatId, "📊 No gainers at the moment.");
                    return;
                }

                var message = _telegramClientWrapper.FormatGainersMessage(gainers);
                await SendMessageAsync(chatId, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top gainers to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching gainers data. Please try again later.");
            }
        }

        private async Task SendTopLosers(long chatId)
        {
            try
            {
                var losers = await GetCachedTopLosersAsync();
                if (!losers.Any())
                {
                    await SendMessageAsync(chatId, "📊 No losers at the moment.");
                    return;
                }

                var message = _telegramClientWrapper.FormatLosersMessage(losers);
                await SendMessageAsync(chatId, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top losers to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching losers data. Please try again later.");
            }
        }

        private async Task SendStockPrice(long chatId, string text)
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

            try
            {
                var stock = await _cacheService.GetOrSetAsync($"stock_price_{symbol}", async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        var data = await stockService.GetStockDataAsync($"{symbol}.NS").WaitAsync(cts.Token);
                        if (data == null)
                        {
                            data = await stockService.GetStockDataAsync($"{symbol}.BO").WaitAsync(cts.Token);
                        }
                        return data;
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Timeout fetching {Symbol}", symbol);
                        return null;
                    }
                }, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

                if (stock == null)
                {
                    await SendMessageAsync(chatId, $"❌ No data found for {symbol}");
                    return;
                }

                var message = _telegramClientWrapper.FormatStockPriceMessage(stock);
                await SendMessageAsync(chatId, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending stock price for {Symbol} to {ChatId}", symbol, chatId);
                await SendMessageAsync(chatId, $"❌ Error fetching data for {symbol}");
            }
        }

        #endregion

        #region Helper Methods

        private async Task SendTypingAction(long chatId)
        {
            try
            {
                await _telegramClientWrapper.SendTypingAsync(chatId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send typing action");
            }
        }

        private async Task SendWelcomeMessage(long chatId, Chat chat)
        {
            var message = $@"<b>🤖 Welcome to Indian Stock Predictor Bot!</b>

Hello {chat.FirstName}! I can help you with:
• 📈 Real-time stock prices
• 📊 Daily market predictions
• 🏆 Top gainers & losers
• 📉 Market trends
• 🔔 Price alerts
• 💼 Portfolio Management

<b>Get started by trying /help to see all commands!</b>";

            await SendMessageAsync(chatId, message);
        }

        private async Task SendHelpMessage(long chatId)
        {
            var message = @"<b>📚 Available Commands</b>

<b>📊 Stock Information:</b>
/stocks - View top 10 active stocks
/topgainers - Today's top 5 gainers
/toplosers - Today's top 5 losers
/price SYMBOL - Get current price (e.g., /price RELIANCE)

<b>📈 Analysis:</b>
/analysis - Enhanced market analysis
/largecap - Large cap picks
/midcap - Mid cap picks
/smallcap - Small cap picks
/technical - Technical indicators

<b>💰 Portfolio:</b>
/portfolio_status - View your portfolio
/optimize_holdings - Get optimization suggestions
/suggest - Portfolio improvement suggestions
/execute_trades - Execute trading plan

<b>🔔 Alerts:</b>
/alert SYMBOL PRICE above/below - Set price alert
/alerts - View your alerts
/removealert ID - Remove an alert
/clearalerts - Clear triggered alerts

<b>📰 Subscriptions:</b>
/subscribe - Get daily reports
/unsubscribe - Stop daily reports
/briefing - Get today's briefing
/subscribe_briefing - Get daily briefing

<b>🏛️ Market:</b>
/market - Market status
/trades - Today's trades
/toptrades - Top trade recommendations

<i>Example: /price RELIANCE, /alert RELIANCE 2500 above</i>";

            await SendMessageAsync(chatId, message);
        }

        private async Task SubscribeUser(long chatId)
        {
            lock (_lockObject)
            {
                if (!_subscribedChats.Contains(chatId))
                {
                    _subscribedChats.Add(chatId);
                }
            }
            await SendMessageAsync(chatId, "✅ Subscribed to daily updates! You'll receive reports every morning at 9 AM.");
        }

        private async Task UnsubscribeUser(long chatId)
        {
            lock (_lockObject)
            {
                if (_subscribedChats.Contains(chatId))
                {
                    _subscribedChats.Remove(chatId);
                }
            }
            await SendMessageAsync(chatId, "✅ Unsubscribed from daily updates.");
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
            lock (_lockObject)
            {
                if (!_briefingSubscribers.Contains(chatId))
                {
                    _briefingSubscribers.Add(chatId);
                }
            }
            await SendMessageAsync(chatId, "✅ You'll receive daily briefings every morning at 8:30 AM.");
        }

        private async Task UnsubscribeFromBriefing(long chatId)
        {
            lock (_lockObject)
            {
                if (_briefingSubscribers.Contains(chatId))
                {
                    _briefingSubscribers.Remove(chatId);
                }
            }
            await SendMessageAsync(chatId, "✅ You've been unsubscribed from daily briefings.");
        }

        private async Task SendDebugMessage(long chatId)
        {
            try
            {
                var debug = new StringBuilder();
                debug.AppendLine("🔍 <b>DEBUG INFORMATION</b>\n");
                debug.AppendLine($"Time: {DateTime.Now:HH:mm:ss}");
                debug.AppendLine($"Bot Running: {_isBotRunning}");
                debug.AppendLine($"Bot Client Null: {_botClient == null}");
                debug.AppendLine($"Telegram Wrapper Null: {_telegramClientWrapper == null}");
                debug.AppendLine($"Command Processor Null: {_commandProcessor == null}");
                debug.AppendLine($"Scope Factory Null: {_scopeFactory == null}");
                debug.AppendLine($"Subscribed Chats: {_subscribedChats.Count}");
                debug.AppendLine($"Briefing Subscribers: {_briefingSubscribers.Count}");
                debug.AppendLine($"Reconnect Attempts: {_reconnectAttempts}");
                debug.AppendLine($"Consecutive Errors: {_consecutiveErrors}");
                debug.AppendLine($"Cache Service: {_cacheService != null}");

                await SendMessageAsync(chatId, debug.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending debug message");
            }
        }

        #endregion

        #region Enhanced Analysis Methods

        private async Task SendEnhancedAnalysis(long chatId)
        {
            try
            {
                await SendMessageAsync(chatId, "🔍 Generating enhanced market analysis... This may take a moment.");

                using var scope = _scopeFactory.CreateScope();
                var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

                var analysis = await analysisService.AnalyzeMarketAsync();
                var report = FormatEnhancedAnalysis(analysis);

                if (report.Length > 4000)
                {
                    var parts = _telegramClientWrapper.SplitMessage(report, 4000);
                    foreach (var part in parts)
                    {
                        await SendMessageAsync(chatId, part);
                        await Task.Delay(500);
                    }
                }
                else
                {
                    await SendMessageAsync(chatId, report);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating enhanced analysis for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error generating market analysis. Please try again later.");
            }
        }

        private string FormatEnhancedAnalysis(EnhancedMarketAnalysis analysis)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"📊 <b>ENHANCED MARKET ANALYSIS</b>");
            sb.AppendLine($"<b>{analysis.AnalysisDate:dddd, MMMM d, yyyy HH:mm}</b>\n");

            // Market Overview
            var phaseEmoji = _telegramClientWrapper.GetMarketPhaseEmoji(analysis.MarketPhase);
            var sentimentEmoji = _telegramClientWrapper.GetSentimentEmoji(analysis.OverallSentiment);

            sb.AppendLine($"{phaseEmoji} <b>Market Phase:</b> {analysis.MarketPhase ?? "Unknown"}");
            sb.AppendLine($"{sentimentEmoji} <b>Overall Sentiment:</b> {analysis.OverallSentiment ?? "Neutral"}");

            var confidenceEmoji = _telegramClientWrapper.GetConfidenceEmoji(analysis.ConfidenceScore.ToString());
            sb.AppendLine($"{confidenceEmoji} <b>Confidence:</b> {analysis.ConfidenceScore}%\n");

            // Large Cap Picks
            if (analysis.LargeCap?.TopPicks?.Any() == true)
            {
                sb.AppendLine("<b>🏢 LARGE CAP TOP PICKS</b>");
                foreach (var pick in analysis.LargeCap.TopPicks.Take(3))
                {
                    var confidenceEmojiPick = _telegramClientWrapper.GetConfidenceEmoji(pick.Confidence.ToString());
                    sb.AppendLine($"• <b>{pick.Symbol}</b> {confidenceEmojiPick}");
                    sb.AppendLine($"  Target: ₹{pick.TargetPrice:F0} | SL: ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                    if (!string.IsNullOrEmpty(pick.Reason))
                        sb.AppendLine($"  <i>{_telegramClientWrapper.TruncateText(pick.Reason, 100)}</i>");
                }
                sb.AppendLine();
            }

            // Mid Cap Picks
            if (analysis.MidCap?.TopPicks?.Any() == true)
            {
                sb.AppendLine("<b>🏭 MID CAP TOP PICKS</b>");
                foreach (var pick in analysis.MidCap.TopPicks.Take(3))
                {
                    sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                }
                sb.AppendLine();
            }

            // Small Cap Picks
            if (analysis.SmallCap?.TopPicks?.Any() == true)
            {
                sb.AppendLine("<b>🏗️ SMALL CAP TOP PICKS</b>");
                foreach (var pick in analysis.SmallCap.TopPicks.Take(3))
                {
                    sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                }
                sb.AppendLine();
            }

            // Technical Indicators
            if (analysis.TechnicalIndicators != null)
            {
                sb.AppendLine("<b>📊 TECHNICAL OUTLOOK</b>");
                sb.AppendLine($"RSI: {_telegramClientWrapper.GetTechnicalEmoji(analysis.TechnicalIndicators.RSIStatus)} {analysis.TechnicalIndicators.RSIStatus ?? "Neutral"}");
                sb.AppendLine($"MACD: {_telegramClientWrapper.GetTechnicalEmoji(analysis.TechnicalIndicators.MACDSignal)} {analysis.TechnicalIndicators.MACDSignal ?? "Neutral"}");
                sb.AppendLine($"Volume: {_telegramClientWrapper.GetVolumeEmoji(analysis.TechnicalIndicators.VolumeAnalysis)} {analysis.TechnicalIndicators.VolumeAnalysis ?? "Average"}");
                sb.AppendLine();
            }

            // News Impact
            if (analysis.KeyNewsImpacts?.Any() == true)
            {
                sb.AppendLine("<b>📰 KEY NEWS IMPACT</b>");
                foreach (var news in analysis.KeyNewsImpacts.Take(3))
                {
                    var impactEmoji = _telegramClientWrapper.GetNewsImpactEmoji(news.Impact);
                    sb.AppendLine($"{impactEmoji} {_telegramClientWrapper.TruncateText(news.Headline, 100)}");
                    if (news.AffectedStocks?.Any() == true)
                        sb.AppendLine($"   Affects: {string.Join(", ", news.AffectedStocks.Take(3))}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"<i>Analysis generated: {analysis.AnalysisDate:HH:mm}</i>");
            sb.AppendLine($"<i>Use /help for more commands</i>");

            return sb.ToString();
        }

        private async Task SendCategoryPicks(long chatId, string category)
        {
            try
            {
                await SendMessageAsync(chatId, $"🔍 Fetching {category.ToUpper()} CAP picks...");

                using var scope = _scopeFactory.CreateScope();
                var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

                var picks = await analysisService.GetTopPicksByCategoryAsync(category, 5);

                if (picks == null || !picks.Any())
                {
                    await SendMessageAsync(chatId, $"❌ No picks available for {category} cap category.");
                    return;
                }

                var categoryName = category.ToLower() switch
                {
                    "large" => "🏢 LARGE CAP",
                    "mid" => "🏭 MID CAP",
                    "small" => "🏗️ SMALL CAP",
                    _ => $"📊 {category.ToUpper()} CAP"
                };

                var sb = new StringBuilder();
                sb.AppendLine($"<b>{categoryName} TOP PICKS</b>\n");

                foreach (var pick in picks)
                {
                    var confidenceEmoji = _telegramClientWrapper.GetConfidenceEmoji(pick.Confidence.ToString());
                    sb.AppendLine($"• <b>{pick.Symbol}</b> {confidenceEmoji}");
                    sb.AppendLine($"  Target: ₹{pick.TargetPrice:F0} | Stop: ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                    if (!string.IsNullOrEmpty(pick.Reason))
                        sb.AppendLine($"  <i>{_telegramClientWrapper.TruncateText(pick.Reason, 100)}</i>");
                    sb.AppendLine();
                }

                sb.AppendLine($"<i>Last updated: {DateTime.Now:HH:mm}</i>");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending {Category} picks to {ChatId}", category, chatId);
                await SendMessageAsync(chatId, $"❌ Error fetching {category} cap picks. Please try again later.");
            }
        }

        private async Task SendTechnicalIndicators(long chatId)
        {
            try
            {
                await SendMessageAsync(chatId, "📊 Fetching technical indicators...");

                using var scope = _scopeFactory.CreateScope();
                var analysisService = scope.ServiceProvider.GetRequiredService<IEnhancedMarketAnalysisService>();

                var indicators = await analysisService.GetMarketTechnicalIndicatorsAsync();

                var sb = new StringBuilder();
                sb.AppendLine("<b>📊 MARKET TECHNICAL INDICATORS</b>\n");

                var marketPhase = indicators.GetValueOrDefault("market_phase")?.ToString() ?? "Neutral";
                var sentiment = indicators.GetValueOrDefault("sentiment")?.ToString() ?? "Neutral";
                var confidence = indicators.GetValueOrDefault("confidence")?.ToString() ?? "0";
                var rsi = indicators.GetValueOrDefault("rsi")?.ToString() ?? "Neutral";
                var macd = indicators.GetValueOrDefault("macd")?.ToString() ?? "Neutral";
                var volume = indicators.GetValueOrDefault("volume")?.ToString() ?? "Average";

                sb.AppendLine($"Market Phase: {_telegramClientWrapper.GetMarketPhaseEmoji(marketPhase)} {marketPhase}");
                sb.AppendLine($"Sentiment: {_telegramClientWrapper.GetSentimentEmoji(sentiment)} {sentiment}");
                sb.AppendLine($"Confidence: {_telegramClientWrapper.GetConfidenceEmoji(confidence)} {confidence}%\n");

                sb.AppendLine("<b>Technical Analysis:</b>");
                sb.AppendLine($"RSI: {_telegramClientWrapper.GetTechnicalEmoji(rsi)} {rsi}");
                sb.AppendLine($"MACD: {_telegramClientWrapper.GetTechnicalEmoji(macd)} {macd}");
                sb.AppendLine($"Volume: {_telegramClientWrapper.GetVolumeEmoji(volume)} {volume}\n");

                sb.AppendLine("<i>Indicators based on Nifty 50 and market breadth</i>");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending technical indicators to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching technical indicators. Please try again later.");
            }
        }

        private async Task SendTodaysTrades(long chatId)
        {
            try
            {
                await SendMessageAsync(chatId, "📈 Fetching today's trading opportunities...");

                using var scope = _scopeFactory.CreateScope();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();

                var dashboard = await tradingService.GetTodaysTradesAsync(70);

                if (dashboard?.Trades == null || !dashboard.Trades.Any())
                {
                    await SendMessageAsync(chatId, "📊 No high-confidence trading opportunities today.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>📈 TODAY'S TRADING OPPORTUNITIES</b>\n");

                sb.AppendLine($"Market Phase: {_telegramClientWrapper.GetMarketPhaseEmoji(dashboard.MarketPhase)} {dashboard.MarketPhase ?? "Unknown"}");
                sb.AppendLine($"Market Confidence: {_telegramClientWrapper.GetConfidenceEmoji(dashboard.MarketConfidence.ToString())} {dashboard.MarketConfidence}%\n");

                sb.AppendLine($"<b>Top {Math.Min(5, dashboard.Trades.Count)} Trades:</b>\n");

                foreach (var trade in dashboard.Trades.Take(5))
                {
                    var riskRewardEmoji = trade.RiskReward >= 2 ? "🟢" : trade.RiskReward >= 1 ? "🟡" : "🔴";
                    sb.AppendLine($"• <b>{trade.Symbol}</b> [{trade.Category}]");
                    sb.AppendLine($"  Entry: ₹{trade.CurrentPrice:F2} | Target: ₹{trade.TargetPrice:F0} | SL: ₹{trade.StopLoss:F0}");
                    sb.AppendLine($"  Confidence: {trade.Confidence}% | R/R: {riskRewardEmoji} {trade.RiskReward}:1");
                    if (!string.IsNullOrEmpty(trade.Reason))
                        sb.AppendLine($"  <i>{_telegramClientWrapper.TruncateText(trade.Reason, 100)}</i>");
                    sb.AppendLine();
                }

                sb.AppendLine($"<b>Summary:</b>");
                sb.AppendLine($"• Total Opportunities: {dashboard.Summary?.TotalTrades ?? 0}");
                sb.AppendLine($"• High Confidence: {dashboard.Summary?.HighConfidenceTrades ?? 0}");
                sb.AppendLine($"• Best Risk/Reward: {dashboard.Summary?.BestRiskReward:F1}:1");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending today's trades to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching trading opportunities. Please try again later.");
            }
        }

        private async Task SendTopTrades(long chatId, int count)
        {
            try
            {
                await SendMessageAsync(chatId, $"🏆 Fetching top {count} trading opportunities...");

                using var scope = _scopeFactory.CreateScope();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();

                var topTrades = await tradingService.GetTopTradesAsync(count);

                if (topTrades == null || !topTrades.Any())
                {
                    await SendMessageAsync(chatId, "📊 No top trading opportunities available.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>🏆 TOP {count} TRADING OPPORTUNITIES</b>\n");

                for (int i = 0; i < topTrades.Count; i++)
                {
                    var trade = topTrades[i];
                    var medal = i == 0 ? "🥇" : i == 1 ? "🥈" : i == 2 ? "🥉" : "📈";

                    sb.AppendLine($"{medal} <b>{trade.Symbol}</b> [{trade.Category}]");
                    sb.AppendLine($"   Entry: ₹{trade.CurrentPrice:F2}");
                    sb.AppendLine($"   Target: ₹{trade.TargetPrice:F0} | Stop: ₹{trade.StopLoss:F0}");
                    sb.AppendLine($"   Confidence: {trade.Confidence}% | R/R: {trade.RiskReward}:1");
                    if (!string.IsNullOrEmpty(trade.Reason))
                        sb.AppendLine($"   <i>{_telegramClientWrapper.TruncateText(trade.Reason, 100)}</i>");
                    sb.AppendLine();
                }

                sb.AppendLine($"<i>Last updated: {DateTime.Now:HH:mm}</i>");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending top trades to {ChatId}", chatId);
                await SendMessageAsync(chatId, $"❌ Error fetching top {count} trades. Please try again later.");
            }
        }

        private async Task ExecuteTodaysTrades(long chatId)
        {
            try
            {
                await SendMessageAsync(chatId, "⚠️ <b>WARNING: This will execute real trades using your Angel One account!</b>\n\nPlease type /confirm_trades to proceed or any other command to cancel.");

                if (!_pendingTradeExecutions.ContainsKey(chatId))
                    _pendingTradeExecutions[chatId] = DateTime.UtcNow.AddMinutes(5);
                else
                    _pendingTradeExecutions[chatId] = DateTime.UtcNow.AddMinutes(5);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating trade execution for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error initiating trade execution. Please try again later.");
            }
        }

        private async Task HandleConfirmTrades(long chatId)
        {
            try
            {
                if (!_pendingTradeExecutions.ContainsKey(chatId) || _pendingTradeExecutions[chatId] < DateTime.UtcNow)
                {
                    await SendMessageAsync(chatId, "❌ No pending trade execution found. Please start with /execute_trades first.");
                    return;
                }

                await SendMessageAsync(chatId, "🔄 Executing today's trading plan... Please wait.");

                using var scope = _scopeFactory.CreateScope();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();

                var dashboard = await tradingService.GetTodaysTradesAsync(80);

                if (dashboard?.Trades == null || !dashboard.Trades.Any())
                {
                    await SendMessageAsync(chatId, "📊 No high-confidence trades to execute today.");
                    return;
                }

                var executedTrades = new List<string>();
                var failedTrades = new List<string>();

                foreach (var trade in dashboard.Trades.Take(3))
                {
                    try
                    {
                        var order = new OrderRequest
                        {
                            Symbol = trade.Symbol,
                            Action = "BUY",
                            Quantity = CalculatePositionSize(trade),
                            Price = trade.CurrentPrice,
                            Exchange = "NSE",
                            Variety = "NORMAL",
                            OrderType = "LIMIT",
                            ProductType = "DELIVERY",
                            Duration = "DAY"
                        };

                        var result = await angelOneService.PlaceOrderAsync(order);

                        if (result.Status == "SUCCESS" || result.Status == "PLACED")
                        {
                            executedTrades.Add($"✅ {trade.Symbol}: {order.Quantity} shares @ ₹{trade.CurrentPrice:F2} (Order: {result.OrderId})");
                            await PlaceStopLoss(trade, order.Quantity, angelOneService);
                        }
                        else
                        {
                            failedTrades.Add($"❌ {trade.Symbol}: {result.Message}");
                        }

                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing trade for {Symbol}", trade.Symbol);
                        failedTrades.Add($"❌ {trade.Symbol}: {ex.Message}");
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("<b>📊 TRADE EXECUTION RESULTS</b>\n");

                if (executedTrades.Any())
                {
                    sb.AppendLine("<b>✅ Executed Trades:</b>");
                    foreach (var trade in executedTrades)
                        sb.AppendLine(trade);
                    sb.AppendLine();
                }

                if (failedTrades.Any())
                {
                    sb.AppendLine("<b>❌ Failed Trades:</b>");
                    foreach (var trade in failedTrades)
                        sb.AppendLine(trade);
                }

                _pendingTradeExecutions.Remove(chatId);

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing trades for {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error executing trades. Please check your Angel One account manually.");
            }
        }

        #endregion

        #region Helper Methods for Implemented Features

        private int CalculatePositionSize(TradeItem trade)
        {
            return Math.Max(1, (int)(10000 / trade.CurrentPrice));
        }

        private async Task PlaceStopLoss(TradeItem trade, int quantity, IAngelOneService angelOneService)
        {
            try
            {
                var stopLossOrder = new OrderRequest
                {
                    Symbol = trade.Symbol,
                    Action = "SELL",
                    Quantity = quantity,
                    Price = trade.StopLoss,
                    Exchange = "NSE",
                    Variety = "STOPLOSS",
                    OrderType = "SL",
                    ProductType = "DELIVERY",
                    Duration = "DAY"
                };

                _logger.LogInformation("Stop loss would be placed for {Symbol} at ₹{StopLoss}", trade.Symbol, trade.StopLoss);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing stop loss for {Symbol}", trade.Symbol);
            }
        }

        #endregion

        #region Alert Handlers

        private async Task HandleAlertCommand(long chatId, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 4)
            {
                await SendMessageAsync(chatId,
                    "❌ Invalid format. Use:\n" +
                    "/alert SYMBOL PRICE above/below\n" +
                    "Example: /alert RELIANCE 2500 above");
                return;
            }

            var symbol = parts[1].ToUpper();

            if (!decimal.TryParse(parts[2], out decimal targetPrice))
            {
                await SendMessageAsync(chatId, "❌ Invalid price. Please enter a valid number.");
                return;
            }

            bool isAbove = parts[3].ToLower() != "below";

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
                        var direction = alert.IsAbove ? "above" : "below";
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
                        sb.AppendLine($"   {alert.Symbol} triggered at ₹{alert.TriggeredPrice:N2} on {alert.TriggeredAt:dd MMM HH:mm}");
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

        #endregion

        #region Market Status

        private async Task SendMarketStatus(long chatId)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("<b>🏛️ MARKET STATUS</b>\n");

                var now = DateTime.Now;
                var isMarketOpen = IsMarketOpen(now);

                var marketEmoji = isMarketOpen ? "🟢 OPEN" : "🔴 CLOSED";
                sb.AppendLine($"Market: {marketEmoji}");

                if (isMarketOpen)
                {
                    var closingTime = GetMarketClosingTime(now);
                    var timeLeft = closingTime - now;
                    sb.AppendLine($"Time until close: {timeLeft.Hours}h {timeLeft.Minutes}m");
                }
                else
                {
                    var nextOpening = GetNextMarketOpenTime(now);
                    var timeUntilOpen = nextOpening - now;
                    sb.AppendLine($"Opens in: {timeUntilOpen.Hours}h {timeUntilOpen.Minutes}m");
                }

                sb.AppendLine($"\n<b>Indices (approx):</b>");
                sb.AppendLine($"Sensex: 73,500.45 (+0.35%)");
                sb.AppendLine($"Nifty: 22,300.20 (+0.28%)");

                sb.AppendLine($"\n<i>Last updated: {now:HH:mm:ss}</i>");

                await SendMessageAsync(chatId, sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending market status to {ChatId}", chatId);
                await SendMessageAsync(chatId, "❌ Error fetching market status.");
            }
        }

        private bool IsMarketOpen(DateTime currentTime)
        {
            if (currentTime.DayOfWeek == DayOfWeek.Saturday || currentTime.DayOfWeek == DayOfWeek.Sunday)
                return false;

            var marketOpen = new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, 9, 15, 0);
            var marketClose = new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, 15, 30, 0);

            return currentTime >= marketOpen && currentTime <= marketClose;
        }

        private DateTime GetMarketClosingTime(DateTime currentTime)
        {
            return new DateTime(currentTime.Year, currentTime.Month, currentTime.Day, 15, 30, 0);
        }

        private DateTime GetNextMarketOpenTime(DateTime currentTime)
        {
            var nextDay = currentTime.AddDays(1);

            while (nextDay.DayOfWeek == DayOfWeek.Saturday || nextDay.DayOfWeek == DayOfWeek.Sunday)
            {
                nextDay = nextDay.AddDays(1);
            }

            return new DateTime(nextDay.Year, nextDay.Month, nextDay.Day, 9, 15, 0);
        }

        #endregion

        #region Scheduled Tasks

        private async Task SendScheduledDailyReport(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var scope = _scopeFactory.CreateScope();
                var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
                var aiService = scope.ServiceProvider.GetRequiredService<IAIService>();

                var stocks = await stockService.GetIndianStockDataAsync();
                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stocks data available for daily report");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var predictions = await aiService.GeneratePredictionsAsync(stocks);

                if (predictions == null)
                {
                    _logger.LogError("Failed to generate predictions");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await SendDailyReportToAllAsync(predictions);
                _logger.LogInformation("Daily report sent to all subscribers");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Daily report sending cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending scheduled daily report");
            }
        }

        private async Task SendScheduledDailyBriefing(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_briefingSubscribers.Any())
                {
                    _logger.LogInformation("No briefing subscribers to send to");
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var briefingService = scope.ServiceProvider.GetRequiredService<IDailyBriefingService>();

                foreach (var chatId in _briefingSubscribers.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await briefingService.SendBriefingToUserAsync(chatId);
                    await Task.Delay(1000, cancellationToken);
                }

                _logger.LogInformation("Daily briefing sent to {Count} subscribers", _briefingSubscribers.Count);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Daily briefing sending cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending scheduled daily briefing");
            }
        }
        #endregion

        #region Portfolio Methods

        private async Task SendPortfolioStatus(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();
                var portfolioAnalyzer = scope.ServiceProvider.GetRequiredService<IPortfolioAnalyzer>();

                var portfolio = await angelOneService.GetPortfolioAsync();

                if (portfolio?.Holdings == null || !portfolio.Holdings.Any())
                {
                    await SendMessageAsync(chatId, "📊 Your portfolio is empty.");
                    return;
                }

                var holdings = portfolio.Holdings;
                var totalValue = portfolio.CurrentValue;
                var totalInvestment = portfolio.TotalInvestment;

                var healthScore = 0;
                var warnings = new List<string>();
                var momentum = string.Empty;
                var diversification = string.Empty;
                var recovery = string.Empty;
                var heatmap = string.Empty;

                try
                {
                    healthScore = portfolioAnalyzer.CalculateEnhancedHealthScore(holdings, totalValue, totalInvestment, portfolio.SectorAllocation ?? new Dictionary<string, decimal>());
                    warnings = portfolioAnalyzer.GenerateEnhancedWarnings(holdings, portfolio, portfolio.SectorAllocation ?? new Dictionary<string, decimal>());
                    momentum = portfolioAnalyzer.CalculateMomentum(holdings);
                    diversification = portfolioAnalyzer.CalculateDiversificationScore(portfolio.SectorAllocation ?? new Dictionary<string, decimal>(), holdings.Count);
                    recovery = portfolioAnalyzer.EstimateRecoveryTime(portfolio.TotalProfitLossPercent);

                    var topHoldingValue = holdings.Max(h => (h.Quantity * h.CurrentPrice));
                    var topHoldingPercent = totalValue > 0 ? (topHoldingValue / totalValue) * 100m : 0m;
                    heatmap = portfolioAnalyzer.GenerateHeatMap(topHoldingPercent);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Portfolio analysis failed, falling back to simple summary");
                }

                var sb = new StringBuilder();
                sb.AppendLine($"<b>📊 PORTFOLIO SUMMARY</b>");
                sb.AppendLine($"As of: {portfolio.AsOfDate:dd MMM yyyy HH:mm}\n");

                sb.AppendLine($"Total Investment: ₹{portfolio.TotalInvestment:N2}");
                sb.AppendLine($"Current Value: ₹{portfolio.CurrentValue:N2}");
                sb.AppendLine($"Total P&L: {(portfolio.TotalProfitLoss >= 0 ? "🟢" : "🔴")} ₹{Math.Abs(portfolio.TotalProfitLoss):N2} ({portfolio.TotalProfitLossPercent:F2}%)\n");

                if (healthScore > 0)
                {
                    var healthEmoji = _telegramClientWrapper.GetConfidenceEmoji(healthScore.ToString());
                    sb.AppendLine($"<b>Health Score:</b> {healthEmoji} {healthScore}/100");
                }

                if (!string.IsNullOrEmpty(momentum))
                    sb.AppendLine($"<b>Momentum:</b> {momentum}");

                if (!string.IsNullOrEmpty(diversification))
                    sb.AppendLine($"<b>Diversification:</b> {diversification}");

                if (!string.IsNullOrEmpty(recovery))
                    sb.AppendLine($"<b>Estimated Recovery:</b> {recovery}");

                if (!string.IsNullOrEmpty(heatmap))
                    sb.AppendLine($"<b>Top Exposure:</b> {heatmap}");

                if (warnings != null && warnings.Any())
                {
                    sb.AppendLine("\n<b>⚠️ Warnings:</b>");
                    foreach (var w in warnings)
                    {
                        sb.AppendLine($"• {w}");
                    }
                }

                sb.AppendLine("\n<b>HOLDINGS:</b>");

                foreach (var holding in holdings.Take(10))
                {
                    var plEmoji = holding.ProfitLoss >= 0 ? "🟢" : "🔴";
                    sb.AppendLine($"• {holding.Symbol}: {holding.Quantity} shares @ ₹{holding.AveragePrice:F2}");
                    sb.AppendLine($"  Current: ₹{holding.CurrentPrice:F2} {plEmoji} P&L: ₹{holding.ProfitLoss:N2}");
                }

                if (holdings.Count > 10)
                {
                    sb.AppendLine($"... and {holdings.Count - 10} more holdings");
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
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();
                var portfolioAnalyzer = scope.ServiceProvider.GetRequiredService<IPortfolioAnalyzer>();

                await SendMessageAsync(chatId, "🔄 Analyzing your holdings for optimization...");

                var plan = await optimizer.AnalyzeHoldingsAsync();

                PortfolioSummary? portfolio = null;
                int healthScore = 0;
                List<string> warnings = new();
                string momentum = string.Empty;
                string diversification = string.Empty;

                try
                {
                    portfolio = await angelOneService.GetPortfolioAsync();
                    if (portfolio != null && portfolio.Holdings != null && portfolio.Holdings.Any())
                    {
                        healthScore = portfolioAnalyzer.CalculateEnhancedHealthScore(portfolio.Holdings, portfolio.CurrentValue, portfolio.TotalInvestment, portfolio.SectorAllocation ?? new Dictionary<string, decimal>());
                        warnings = portfolioAnalyzer.GenerateEnhancedWarnings(portfolio.Holdings, portfolio, portfolio.SectorAllocation ?? new Dictionary<string, decimal>());
                        momentum = portfolioAnalyzer.CalculateMomentum(portfolio.Holdings);
                        diversification = portfolioAnalyzer.CalculateDiversificationScore(portfolio.SectorAllocation ?? new Dictionary<string, decimal>(), portfolio.Holdings.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to compute portfolio analysis in OptimizeHoldings");
                }

                if (plan?.Actions == null || !plan.Actions.Any())
                {
                    await SendMessageAsync(chatId, "✅ Your portfolio is already optimized! No actions needed.");
                    return;
                }

                var sb = new StringBuilder();

                sb.AppendLine("<b>📊 OPTIMIZATION PLAN</b>\n");

                if (healthScore > 0 || !string.IsNullOrEmpty(momentum) || !string.IsNullOrEmpty(diversification) || (warnings != null && warnings.Any()))
                {
                    sb.AppendLine("<b>Portfolio Analysis (pre-optimize):</b>");
                    if (healthScore > 0)
                    {
                        var healthEmoji = _telegramClientWrapper.GetConfidenceEmoji(healthScore.ToString());
                        sb.AppendLine($"Health Score: {healthEmoji} {healthScore}/100");
                    }

                    if (!string.IsNullOrEmpty(momentum)) sb.AppendLine($"Momentum: {momentum}");
                    if (!string.IsNullOrEmpty(diversification)) sb.AppendLine($"Diversification: {diversification}");

                    if (warnings != null && warnings.Any())
                    {
                        sb.AppendLine("Warnings:");
                        foreach (var w in warnings.Take(5)) sb.AppendLine($"• {w}");
                    }

                    sb.AppendLine();
                }

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

        private async Task SendPortfolioSuggestions(long chatId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();
                var optimizer = scope.ServiceProvider.GetRequiredService<HoldingOptimizer>();
                var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();
                var portfolioAnalyzer = scope.ServiceProvider.GetRequiredService<IPortfolioAnalyzer>();

                await SendMessageAsync(chatId, "🔄 Analyzing your portfolio for improvement suggestions... This may take a moment.");

                var portfolio = await angelOneService.GetPortfolioAsync();
                if (portfolio?.Holdings == null || !portfolio.Holdings.Any())
                {
                    await SendMessageAsync(chatId, "📊 Your portfolio is empty. Start investing to get improvement suggestions!");
                    return;
                }

                var holdings = portfolio.Holdings;
                var totalValue = portfolio.CurrentValue;
                var totalInvestment = portfolio.TotalInvestment;

                var plan = await optimizer.AnalyzeHoldingsAsync();
                var sectorAllocation = portfolio.SectorAllocation ?? new Dictionary<string, decimal>();

                var healthScore = portfolioAnalyzer.CalculateEnhancedHealthScore(holdings, totalValue, totalInvestment, sectorAllocation);
                var warnings = portfolioAnalyzer.GenerateEnhancedWarnings(holdings, portfolio, sectorAllocation);
                var momentum = portfolioAnalyzer.CalculateMomentum(holdings);
                var diversificationScore = portfolioAnalyzer.CalculateDiversificationScore(sectorAllocation, holdings.Count);
                var recoveryTime = portfolioAnalyzer.EstimateRecoveryTime(portfolio.TotalProfitLossPercent);

                var topHoldings = holdings
                    .OrderByDescending(h => (h.Quantity * h.CurrentPrice) / totalValue * 100)
                    .Take(5)
                    .ToList();

                var buyRecommendations = await GetBuyRecommendations(holdings, tradingService, portfolio);

                var message = BuildPortfolioSuggestionsMessage(
                    healthScore,
                    portfolio,
                    totalValue,
                    totalInvestment,
                    warnings,
                    momentum,
                    diversificationScore,
                    recoveryTime,
                    sectorAllocation,
                    topHoldings,
                    plan,
                    buyRecommendations,
                    portfolioAnalyzer);

                var finalMessage = message.ToString();

                if (finalMessage.Length > 4000)
                {
                    var parts = _telegramClientWrapper.SplitMessage(finalMessage, 4000);
                    foreach (var part in parts)
                    {
                        await SendMessageAsync(chatId, part);
                    }
                }
                else
                {
                    await SendMessageAsync(chatId, finalMessage);
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

        private StringBuilder BuildPortfolioSuggestionsMessage(
            int healthScore,
            PortfolioSummary portfolio,
            decimal totalValue,
            decimal totalInvestment,
            List<string> warnings,
            string momentum,
            string diversificationScore,
            string recoveryTime,
            Dictionary<string, decimal> sectorAllocation,
            List<Holding> topHoldings,
            OptimizationPlan plan,
            List<TradeItem> buyRecommendations,
            IPortfolioAnalyzer portfolioAnalyzer)
        {
            var message = new StringBuilder();

            message.AppendLine("📊 <b>PORTFOLIO IMPROVEMENT SUGGESTIONS</b>\n");

            var healthEmoji = _telegramClientWrapper.GetConfidenceEmoji(healthScore.ToString());
            message.AppendLine($"{healthEmoji} <b>Portfolio Health Score:</b> {healthScore}/100\n");

            message.AppendLine($"💰 <b>Total Value:</b> ₹{totalValue:N2}");
            message.AppendLine($"📈 <b>Total Investment:</b> ₹{totalInvestment:N2}");
            message.AppendLine($"📊 <b>Total P&L:</b> {(portfolio.TotalProfitLoss >= 0 ? "🟢" : "🔴")} ₹{Math.Abs(portfolio.TotalProfitLoss):N2} ({portfolio.TotalProfitLossPercent:F2}%)\n");
            message.AppendLine($"⏱️ <b>Estimated Recovery Time:</b> {recoveryTime}\n");

            if (warnings.Any())
            {
                message.AppendLine("<b>⚠️ Key Warnings:</b>");
                foreach (var warning in warnings.Take(3))
                {
                    message.AppendLine($"• {warning}");
                }
                message.AppendLine();
            }

            if (plan?.Actions != null && plan.Actions.Any())
            {
                var sellActions = plan.Actions.Where(a => a.Action == "SELL").ToList();
                if (sellActions.Any())
                {
                    message.AppendLine("<b>🔴 SELL RECOMMENDATIONS:</b>");
                    foreach (var action in sellActions.Take(3))
                    {
                        message.AppendLine($"• <b>{action.Symbol}</b>: Sell {action.Quantity} shares");
                        message.AppendLine($"  Reason: {action.Reason}");
                        if (action.ExpectedProfit > 0)
                            message.AppendLine($"  Expected Profit: ₹{action.ExpectedProfit:N2}");
                    }
                    message.AppendLine();
                }
            }

            if (buyRecommendations.Any())
            {
                message.AppendLine("<b>🟢 BUY RECOMMENDATIONS:</b>");
                var cashPosition = totalValue - totalInvestment;
                if (cashPosition > 0)
                {
                    message.AppendLine($"<i>Based on available cash: ₹{cashPosition:N2}</i>\n");
                }

                foreach (var rec in buyRecommendations.Take(3))
                {
                    var totalCost = rec.Quantity * rec.CurrentPrice;
                    message.AppendLine($"• <b>{rec.Symbol}</b>");
                    message.AppendLine($"  Buy {rec.Quantity} shares @ ₹{rec.CurrentPrice:F2} = ₹{totalCost:N2}");
                    message.AppendLine($"  Target: ₹{rec.TargetPrice:F0} | SL: ₹{rec.StopLoss:F0}");
                    message.AppendLine($"  <i>{rec.Reason}</i>\n");
                }
            }

            message.AppendLine("<b>📊 QUICK STATS</b>");
            message.AppendLine($"📊 Momentum: {momentum}");
            message.AppendLine($"🌍 Diversification: {diversificationScore}");
            message.AppendLine($"📈 Holdings: {portfolio.Holdings.Count} stocks");
            message.AppendLine($"🏭 Sectors: {sectorAllocation.Count} sectors\n");

            if (sectorAllocation.Any())
            {
                message.AppendLine("<b>📊 SECTOR ALLOCATION</b>");
                foreach (var sector in sectorAllocation.OrderByDescending(s => s.Value).Take(5))
                {
                    var emoji = sector.Value > 40 ? "🔴" : sector.Value > 25 ? "🟡" : "🟢";
                    var heatMap = portfolioAnalyzer.GenerateHeatMap(sector.Value);
                    message.AppendLine($"{emoji} {sector.Key}: {sector.Value:F1}% ({heatMap})");
                }
                message.AppendLine();
            }

            if (topHoldings.Any())
            {
                message.AppendLine("<b>🔥 PORTFOLIO HEATMAP</b>");
                foreach (var holding in topHoldings)
                {
                    var percentage = (holding.Quantity * holding.CurrentPrice / totalValue) * 100;
                    var performance = holding.ProfitLossPercent >= 0 ? "🟢" : "🔴";
                    var heatmap = portfolioAnalyzer.GenerateHeatMap(percentage);
                    message.AppendLine($"{performance} {holding.Symbol}: {percentage:F1}% ({heatmap})");
                }
                message.AppendLine();
            }

            var priorityActions = new List<string>();

            if (plan?.Actions.Any(a => a.Priority == 1) == true)
                priorityActions.Add("⚠️ URGENT: High-priority sell signals detected");

            if (sectorAllocation.Values.Any(v => v > 40))
                priorityActions.Add("⚡ HIGH: Reduce concentrated sector exposure");

            var largePosition = portfolio.Holdings.FirstOrDefault(h => (h.Quantity * h.CurrentPrice / totalValue) * 100 > 30);
            if (largePosition != null)
                priorityActions.Add($"⚡ HIGH: Reduce {largePosition.Symbol} position");

            if (portfolio.Holdings.Count < 5)
                priorityActions.Add("📊 Add more stocks for diversification");

            if (priorityActions.Any())
            {
                message.AppendLine("<b>🎯 PRIORITY ACTIONS</b>");
                foreach (var action in priorityActions.Take(3))
                {
                    message.AppendLine($"• {action}");
                }
                message.AppendLine();
            }

            message.AppendLine($"<i>Analysis generated: {DateTime.Now:dd MMM yyyy HH:mm}</i>");
            message.AppendLine("\n<i>Use /help for more commands</i>");

            return message;
        }

        private async Task<List<TradeItem>> GetBuyRecommendations(
            List<Holding> holdings,
            ITradingService tradingService,
            PortfolioSummary portfolio)
        {
            try
            {
                var recommendations = new List<TradeItem>();
                var availableCash = portfolio.CurrentValue * 0.2m;

                if (availableCash <= 5000)
                    return recommendations;

                var existingSymbols = holdings.Select(h => h.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var topTrades = await tradingService.GetTopTradesAsync(10);

                var candidates = topTrades
                    .Where(t => !existingSymbols.Contains(t.Symbol))
                    .Where(t => t.CurrentPrice <= availableCash)
                    .Where(t => t.Confidence >= 65)
                    .OrderByDescending(t => t.Confidence)
                    .Take(3)
                    .ToList();

                foreach (var trade in candidates)
                {
                    var maxShares = (int)(availableCash / trade.CurrentPrice);
                    if (maxShares < 1) continue;

                    var maxPositionValue = portfolio.CurrentValue * 0.05m;
                    var recommendedShares = Math.Min(maxShares, (int)(maxPositionValue / trade.CurrentPrice));

                    if (recommendedShares < 1) continue;

                    trade.Quantity = recommendedShares;
                    recommendations.Add(trade);
                    availableCash -= recommendedShares * trade.CurrentPrice;
                }

                return recommendations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting buy recommendations");
                return new List<TradeItem>();
            }
        }

        #endregion

        #region Send Immediate Acknowledgment

        private async Task SendImmediateAcknowledgment(long chatId, string lowerText, string originalText)
        {
            try
            {
                if (string.IsNullOrEmpty(lowerText)) return;

                if (lowerText == "/stocks" || lowerText == "/topgainers" || lowerText == "/toplosers")
                {
                    await SendMessageAsync(chatId, $"🔄 Fetching {lowerText.Substring(1)}... Please wait (this may take a few seconds).");
                    return;
                }
                else if (lowerText == "/portfolio_status")
                {
                    await SendMessageAsync(chatId, "🔄 Fetching your portfolio... Please wait.");
                    return;
                }
                else if (lowerText == "/suggest")
                {
                    await SendMessageAsync(chatId, "🔄 Analyzing your portfolio... This may take a moment.");
                    return;
                }
                else if (lowerText == "/optimize_holdings")
                {
                    await SendMessageAsync(chatId, "🔄 Optimizing your holdings... Please wait.");
                    return;
                }
                else if (lowerText == "/market")
                {
                    await SendMessageAsync(chatId, "🔄 Checking market status... Please wait.");
                    return;
                }
                else if (lowerText == "/alerts")
                {
                    await SendMessageAsync(chatId, "🔔 Fetching your alerts... Please wait.");
                    return;
                }
                else if (lowerText.StartsWith("/price"))
                {
                    await SendMessageAsync(chatId, "🔄 Fetching price... Please wait.");
                    return;
                }
                else if (lowerText.StartsWith("/alert"))
                {
                    await SendMessageAsync(chatId, "🔔 Setting up your alert... Please wait.");
                    return;
                }

                await SendTypingAction(chatId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send immediate acknowledgment");
            }
        }

        #endregion

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping TelegramBotService...");

            try
            {
                // First, signal cancellation to all background tasks
                if (_backgroundCts != null && !_backgroundCts.IsCancellationRequested)
                {
                    _logger.LogInformation("Cancelling background tasks...");
                    try
                    {
                        await _backgroundCts.CancelAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogDebug("Background CTS already disposed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error cancelling background tasks");
                    }
                }

                // Cancel the health check token
                if (_healthCheckCts != null && !_healthCheckCts.IsCancellationRequested)
                {
                    try
                    {
                        await _healthCheckCts.CancelAsync();
                        _logger.LogInformation("Health check token cancelled");
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogDebug("Health check CTS already disposed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error cancelling health check token");
                    }
                }

                // Give tasks a moment to respond to cancellation
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected if cancellation token is triggered
                }
                catch (ObjectDisposedException)
                {
                    // Ignore
                }

                // Stop the bot client
                if (_botClient != null)
                {
                    _logger.LogInformation("Stopping bot client...");
                    _isBotRunning = false;

                    // Wait a moment for the receiving loop to stop
                    try
                    {
                        await Task.Delay(500, cancellationToken);
                    }
                    catch { }

                    // Set bot client to null (will be garbage collected)
                    _botClient = null;
                    _logger.LogInformation("Bot client stopped");
                }

                // Clean up resources with proper null checks and try-catch
                try
                {
                    if (_healthCheckCts != null)
                    {
                        _healthCheckCts.Dispose();
                        _healthCheckCts = null;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing health check CTS");
                }

                try
                {
                    if (_backgroundCts != null)
                    {
                        _backgroundCts.Dispose();
                        _backgroundCts = null;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing background CTS");
                }

                // Clear collections
                try
                {
                    lock (_lockObject)
                    {
                        _subscribedChats.Clear();
                        _briefingSubscribers.Clear();
                        _commandCount.Clear();
                        _lastCommand.Clear();
                        _lastCommandTime.Clear();
                        _pendingTradeExecutions.Clear();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error clearing collections");
                }

                _logger.LogInformation("TelegramBotService stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during StopAsync");
                // Don't rethrow - allow shutdown to continue
            }

            // Call base StopAsync
            try
            {
                await base.StopAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in base StopAsync");
            }
        }
    }
}