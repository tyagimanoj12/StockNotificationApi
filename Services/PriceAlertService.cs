using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class PriceAlertService : BackgroundService, IPriceAlertService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<PriceAlertService> _logger;

        // In-memory alerts storage
        private static readonly Dictionary<long, List<UserAlert>> _userAlerts = new();
        private static readonly object _alertLock = new();
        private static int _nextId = 1;

        public PriceAlertService(
            IServiceProvider services,
            ILogger<PriceAlertService> logger)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                    Id = _nextId++,
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
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in price alert service");
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                }
            }

            _logger.LogInformation("Price Alert Service stopped");
        }

        private async Task CheckAlerts(CancellationToken stoppingToken)
        {
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

            using var scope = _services.CreateScope();
            var stockService = scope.ServiceProvider.GetRequiredService<IStockService>();
            var telegram = scope.ServiceProvider.GetRequiredService<ITelegramBotService>();

            // Group by symbol to batch API calls
            var symbols = alertsToCheck.Select(a => a.alert.Symbol).Distinct().ToList();
            var stockData = new Dictionary<string, StockData?>();

            foreach (var symbol in symbols)
            {
                try
                {
                    // Try NSE first, then BSE
                    var stock = await stockService.GetStockDataAsync($"{symbol}.NS");
                    if (stock == null)
                        stock = await stockService.GetStockDataAsync($"{symbol}.BO");

                    stockData[symbol] = stock;

                    // Small delay to avoid rate limiting
                    await Task.Delay(100, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching price for {Symbol}", symbol);
                    stockData[symbol] = null;
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
            foreach (var (chatId, alert, price) in triggeredAlerts)
            {
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
    }
}