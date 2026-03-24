using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Collections.Concurrent;

namespace StockNotificationApi.Services
{
    public class PriceAlertService : BackgroundService, IPriceAlertService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<PriceAlertService> _logger;
        private readonly ICacheService _cacheService;
        private static readonly SemaphoreSlim _alertCheckLock = new(1, 1);

        // Use constants from Constants.cs
        private const int CHECK_INTERVAL_SECONDS = 30;
        private const int ERROR_RETRY_SECONDS = 60;
        private const int PRICE_CACHE_DURATION_SECONDS = 60; // Cache prices for 1 minute

        // In-memory alerts storage
        private static readonly Dictionary<long, List<UserAlert>> _userAlerts = new();
        private static readonly object _alertLock = new();
        private static int _nextId = 1;

        // Track last alert check to avoid concurrent checks
        private static DateTime _lastCheckTime = DateTime.MinValue;
        private static bool _isChecking = false;

        public PriceAlertService(
            IServiceProvider services,
            ILogger<PriceAlertService> logger,
            ICacheService cacheService)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        }

        public class UserAlert
        {
            public int Id { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public decimal TargetPrice { get; set; }
            public bool IsAbove { get; set; }
            public bool IsTriggered { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? TriggeredAt { get; set; }
            public decimal? TriggeredPrice { get; set; }
        }

        public async Task AddAlertAsync(long chatId, string symbol, decimal targetPrice, bool isAbove)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

            if (targetPrice <= 0)
                throw new ArgumentException("Target price must be greater than 0", nameof(targetPrice));

            lock (_alertLock)
            {
                if (!_userAlerts.ContainsKey(chatId))
                    _userAlerts[chatId] = new List<UserAlert>();

                // Check if user already has this alert
                var existing = _userAlerts[chatId]
                    .FirstOrDefault(a => !a.IsTriggered &&
                        a.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                        a.TargetPrice == targetPrice &&
                        a.IsAbove == isAbove);

                if (existing != null)
                {
                    _logger.LogInformation("Alert already exists for user {ChatId}: {Symbol} at {Target}",
                        chatId, symbol, targetPrice);
                    return;
                }

                _userAlerts[chatId].Add(new UserAlert
                {
                    Id = Interlocked.Increment(ref _nextId),
                    Symbol = symbol.ToUpper(),
                    TargetPrice = targetPrice,
                    IsAbove = isAbove,
                    CreatedAt = DateTime.Now
                });

                _logger.LogInformation("Alert added for user {ChatId}: {Symbol} at ₹{Target} ({Direction})",
                    chatId, symbol, targetPrice, isAbove ? "above" : "below");
            }

            await Task.CompletedTask;
        }

        public Task<List<UserAlert>> GetUserAlertsAsync(long chatId)
        {
            lock (_alertLock)
            {
                if (!_userAlerts.ContainsKey(chatId))
                    return Task.FromResult(new List<UserAlert>());

                return Task.FromResult(_userAlerts[chatId]
                    .OrderByDescending(a => a.CreatedAt)
                    .ToList());
            }
        }

        public Task<bool> RemoveAlertAsync(long chatId, int alertId)
        {
            lock (_alertLock)
            {
                if (!_userAlerts.ContainsKey(chatId))
                    return Task.FromResult(false);

                var alert = _userAlerts[chatId].FirstOrDefault(a => a.Id == alertId);
                if (alert != null)
                {
                    _userAlerts[chatId].Remove(alert);
                    _logger.LogInformation("Alert {AlertId} removed for user {ChatId}", alertId, chatId);
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        }

        public Task ClearTriggeredAlertsAsync(long chatId)
        {
            lock (_alertLock)
            {
                if (_userAlerts.ContainsKey(chatId))
                {
                    _userAlerts[chatId].RemoveAll(a => a.IsTriggered);
                    _logger.LogInformation("Cleared triggered alerts for user {ChatId}", chatId);
                }
            }
            return Task.CompletedTask;
        }

        // FIX: Add the missing method
        public Task<int> GetActiveAlertCountAsync(long chatId)
        {
            lock (_alertLock)
            {
                if (!_userAlerts.ContainsKey(chatId))
                    return Task.FromResult(0);

                return Task.FromResult(_userAlerts[chatId].Count(a => !a.IsTriggered));
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Price Alert Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAlerts(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(CHECK_INTERVAL_SECONDS), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Price Alert Service stopping gracefully");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in price alert service");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ERROR_RETRY_SECONDS), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("Price Alert Service stopped");
        }

        private async Task CheckAlerts(CancellationToken stoppingToken)
        {
            // Prevent concurrent alert checks
            if (_isChecking)
            {
                _logger.LogDebug("Alert check already in progress, skipping...");
                return;
            }

            await _alertCheckLock.WaitAsync(stoppingToken);
            try
            {
                _isChecking = true;
                _lastCheckTime = DateTime.UtcNow;

                List<(long chatId, UserAlert alert)> alertsToCheck;

                // Get snapshot of alerts to check
                lock (_alertLock)
                {
                    alertsToCheck = _userAlerts
                        .SelectMany(kvp => kvp.Value
                            .Where(a => !a.IsTriggered)
                            .Select(a => (kvp.Key, a)))
                        .ToList();
                }

                if (!alertsToCheck.Any())
                    return;

                _logger.LogDebug("Checking {Count} alerts for {Symbols} unique symbols",
                    alertsToCheck.Count, alertsToCheck.Select(a => a.alert.Symbol).Distinct().Count());

                using var scope = _services.CreateScope();
                var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();
                var telegram = scope.ServiceProvider.GetRequiredService<ITelegramBotService>();

                // Group by symbol to batch API calls
                var symbols = alertsToCheck.Select(a => a.alert.Symbol).Distinct().ToList();
                var stockData = new Dictionary<string, StockData?>();

                // First, try to get from cache
                var symbolsToFetch = new List<string>();

                foreach (var symbol in symbols)
                {
                    var cached = _cacheService.Get<StockData>($"alert_price_{symbol}");
                    if (cached != null && DateTime.UtcNow - cached.Timestamp < TimeSpan.FromSeconds(PRICE_CACHE_DURATION_SECONDS))
                    {
                        stockData[symbol] = cached;
                        _logger.LogDebug("Using cached price for {Symbol}: ₹{Price}", symbol, cached.Price);
                    }
                    else
                    {
                        symbolsToFetch.Add(symbol);
                    }
                }

                // Fetch fresh prices for symbols not in cache
                if (symbolsToFetch.Any())
                {
                    _logger.LogDebug("Fetching fresh prices for {Count} symbols", symbolsToFetch.Count);

                    try
                    {
                        // Use bulk API to get all quotes at once
                        var quotes = await angelOneService.GetMultipleQuotesAsync(symbolsToFetch);

                        foreach (var quote in quotes.Where(q => q != null))
                        {
                            stockData[quote.Symbol] = quote;
                            // Cache the price for 1 minute
                            _cacheService.Set($"alert_price_{quote.Symbol}", quote, TimeSpan.FromSeconds(PRICE_CACHE_DURATION_SECONDS));
                            _logger.LogDebug("Cached price for {Symbol}: ₹{Price}", quote.Symbol, quote.Price);
                        }

                        // Check for any symbols that weren't fetched
                        var missingSymbols = symbolsToFetch.Except(stockData.Keys).ToList();
                        foreach (var symbol in missingSymbols)
                        {
                            _logger.LogWarning("Could not fetch price for {Symbol}", symbol);
                            stockData[symbol] = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Bulk quote fetch failed for {Count} symbols, falling back to individual calls", symbolsToFetch.Count);

                        // Fallback to individual calls if bulk fails
                        foreach (var symbol in symbolsToFetch)
                        {
                            if (stoppingToken.IsCancellationRequested)
                                break;

                            try
                            {
                                var stock = await angelOneService.GetLiveQuoteAsync(symbol);
                                stockData[symbol] = stock;
                                if (stock != null)
                                {
                                    _cacheService.Set($"alert_price_{symbol}", stock, TimeSpan.FromSeconds(PRICE_CACHE_DURATION_SECONDS));
                                }
                                await Task.Delay(100, stoppingToken);
                            }
                            catch (Exception ex2)
                            {
                                _logger.LogError(ex2, "Error fetching price for {Symbol}", symbol);
                                stockData[symbol] = null;
                            }
                        }
                    }
                }

                var triggeredAlerts = new List<(long chatId, UserAlert alert, decimal price)>();

                lock (_alertLock)
                {
                    foreach (var (chatId, alert) in alertsToCheck)
                    {
                        if (!stockData.TryGetValue(alert.Symbol, out var stock) || stock == null)
                        {
                            _logger.LogDebug("No stock data available for {Symbol}, skipping alert check", alert.Symbol);
                            continue;
                        }

                        bool shouldTrigger = alert.IsAbove
                            ? stock.Price >= alert.TargetPrice
                            : stock.Price <= alert.TargetPrice;

                        if (shouldTrigger)
                        {
                            alert.IsTriggered = true;
                            alert.TriggeredAt = DateTime.Now;
                            alert.TriggeredPrice = stock.Price;
                            triggeredAlerts.Add((chatId, alert, stock.Price));
                            _logger.LogDebug("Alert triggered for {Symbol} at ₹{Price}", alert.Symbol, stock.Price);
                        }
                    }
                }

                // Send notifications outside lock
                if (triggeredAlerts.Any())
                {
                    _logger.LogInformation("Triggered {Count} alerts", triggeredAlerts.Count);

                    foreach (var (chatId, alert, price) in triggeredAlerts)
                    {
                        if (stoppingToken.IsCancellationRequested)
                            break;

                        try
                        {
                            var direction = alert.IsAbove ? "above" : "below";
                            var emoji = alert.IsAbove ? "📈" : "📉";

                            await telegram.SendMessageAsync(chatId,
                                $"{emoji} <b>Price Alert Triggered!</b>\n\n" +
                                $"{alert.Symbol} has moved {direction} ₹{alert.TargetPrice:F2}\n" +
                                $"Current Price: ₹{price:F2}\n" +
                                $"Set on: {alert.CreatedAt:dd MMM yyyy HH:mm}",
                                Telegram.Bot.Types.Enums.ParseMode.Html);

                            _logger.LogInformation("Alert triggered for user {ChatId}: {Symbol} at ₹{Price}",
                                chatId, alert.Symbol, price);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error sending alert notification to {ChatId}", chatId);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Alert check cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking alerts");
            }
            finally
            {
                _isChecking = false;
                _alertCheckLock.Release();
            }
        }

        // Optional: Method to get all alerts (for admin purposes)
        public Task<Dictionary<long, List<UserAlert>>> GetAllAlertsAsync()
        {
            lock (_alertLock)
            {
                // Return a copy to prevent modification
                var copy = _userAlerts.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList()
                );
                return Task.FromResult(copy);
            }
        }

        // Optional: Method to cleanup old triggered alerts
        public Task CleanupOldAlertsAsync(int daysOld = 7)
        {
            var cutoffDate = DateTime.Now.AddDays(-daysOld);

            lock (_alertLock)
            {
                foreach (var chatId in _userAlerts.Keys.ToList())
                {
                    _userAlerts[chatId].RemoveAll(a =>
                        a.IsTriggered && a.TriggeredAt < cutoffDate);
                }
            }

            _logger.LogInformation("Cleaned up alerts older than {DaysOld} days", daysOld);
            return Task.CompletedTask;
        }

        // Method to get alert statistics
        public (int TotalAlerts, int TriggeredAlerts, int ActiveAlerts) GetAlertStats()
        {
            lock (_alertLock)
            {
                var totalAlerts = _userAlerts.Values.Sum(list => list.Count);
                var triggeredAlerts = _userAlerts.Values.Sum(list => list.Count(a => a.IsTriggered));
                var activeAlerts = totalAlerts - triggeredAlerts;

                return (totalAlerts, triggeredAlerts, activeAlerts);
            }
        }
    }
}