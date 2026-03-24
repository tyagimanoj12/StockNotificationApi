using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Collections.Concurrent;

namespace StockNotificationApi.Services
{
    public class AutoTradingService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoTradingService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory; // ADD THIS


        // Use constants from Constants.cs
        private const decimal MAX_POSITION_SIZE_PERCENT = TradingConstants.MAX_POSITION_SIZE_PERCENT;
        private const decimal MAX_DAILY_LOSS_PERCENT = TradingConstants.MAX_DAILY_LOSS_PERCENT;
        private const decimal MAX_PORTFOLIO_RISK = TradingConstants.MAX_PORTFOLIO_RISK;
        private const int MAX_TRADES_PER_DAY = TradingConstants.MAX_TRADES_PER_DAY;

        // Use ConcurrentBag for thread-safe collection
        private static readonly ConcurrentBag<TradeExecution> _todayTrades = new();
        private static readonly ConcurrentDictionary<string, TradeExecution> _activeTrades = new();
        private static readonly SemaphoreSlim _tradeLock = new(1, 1);

        // Track daily P&L
        private static decimal _dailyPnL = 0;
        private static DateTime _lastPnLReset = DateTime.UtcNow.Date;

        // Cache for portfolio to avoid multiple fetches
        private static PortfolioSummary? _cachedPortfolio;
        private static DateTime _lastPortfolioFetch = DateTime.MinValue;
        private static readonly TimeSpan PortfolioCacheDuration = TimeSpan.FromSeconds(30);

        // Track daily loss limit
        private static bool _dailyLossLimitHit = false;

        public AutoTradingService(
            IServiceProvider serviceProvider,
            ILogger<AutoTradingService> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory)); ;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Auto-Trading Service Started");

            var autoTradingEnabled = _configuration.GetValue<bool>("Trading:AutoTradingEnabled", false);
            if (!autoTradingEnabled)
            {
                _logger.LogInformation("Auto-trading is disabled. Set Trading:AutoTradingEnabled=true to enable.");
                return;
            }

            // Start reset task with proper cancellation
            _ = Task.Run(() => ResetDailyPnL(stoppingToken), stoppingToken);

            await WaitForMarketOpen(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!IsMarketOpen())
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                        continue;
                    }

                    // Reset daily loss flag if new day
                    if (_lastPnLReset.Date < DateTime.UtcNow.Date)
                    {
                        _dailyLossLimitHit = false;
                    }

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();
                        var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();

                        // Fetch portfolio ONCE at the beginning of the loop
                        var portfolio = await GetPortfolioWithCache(angelOneService);
                        var holdings = portfolio?.Holdings ?? new List<Holding>();

                        if (_dailyLossLimitHit || await HasExceededDailyLoss(portfolio))
                        {
                            _logger.LogWarning("Daily loss limit exceeded. Stopping trading for today.");
                            _dailyLossLimitHit = true;
                            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                            continue;
                        }

                        var dashboard = await tradingService.GetTodaysTradesAsync(minConfidence: 70);

                        if (dashboard?.Trades == null || !dashboard.Trades.Any())
                        {
                            _logger.LogInformation("No trading signals available today");
                            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                            continue;
                        }

                        var todayTradeCount = GetTodayTradeCount();
                        if (todayTradeCount >= MAX_TRADES_PER_DAY)
                        {
                            _logger.LogInformation("Maximum trades per day ({Max}) reached.", MAX_TRADES_PER_DAY);
                            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                            continue;
                        }

                        var remainingTrades = MAX_TRADES_PER_DAY - todayTradeCount;
                        foreach (var trade in dashboard.Trades.OrderByDescending(t => t.RiskReward).Take(remainingTrades))
                        {
                            // Check cancellation before each trade
                            stoppingToken.ThrowIfCancellationRequested();

                            // Update current price from Angel One for accuracy
                            var liveQuote = await angelOneService.GetLiveQuoteAsync(trade.Symbol);
                            if (liveQuote != null && liveQuote.Price > 0)
                            {
                                trade.CurrentPrice = liveQuote.Price;
                            }

                            if (await ShouldTrade(trade, portfolio, holdings, angelOneService))
                            {
                                await ExecuteTrade(trade, portfolio, angelOneService);
                            }
                        }
                    }

                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Auto-trading service stopping gracefully");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in auto-trading loop");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Auto-Trading Service stopped");
        }

        private async Task<PortfolioSummary> GetPortfolioWithCache(IAngelOneService angelOneService)
        {
            // Return cached portfolio if still valid
            if (_cachedPortfolio != null && DateTime.UtcNow - _lastPortfolioFetch < PortfolioCacheDuration)
            {
                return _cachedPortfolio;
            }

            // Fetch fresh portfolio
            try
            {
                _cachedPortfolio = await angelOneService.GetPortfolioAsync();
                _lastPortfolioFetch = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching portfolio");
                _cachedPortfolio = null;
            }

            return _cachedPortfolio ?? new PortfolioSummary
            {
                CurrentValue = 0,
                TotalInvestment = 0,
                Holdings = new List<Holding>()
            };
        }

        private int GetTodayTradeCount()
        {
            var today = DateTime.UtcNow.Date;
            return _todayTrades.Count(t => t.ExecutedAt.Date == today);
        }

        private async Task ResetDailyPnL(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Wait until midnight
                    var now = DateTime.UtcNow;
                    var midnight = now.Date.AddDays(1);
                    var delay = midnight - now;

                    try
                    {
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    stoppingToken.ThrowIfCancellationRequested();

                    _logger.LogInformation("Resetting daily P&L...");

                    // Reset static P&L
                    await _tradeLock.WaitAsync(stoppingToken);
                    try
                    {
                        _logger.LogInformation("Daily P&L reset: Yesterday's P&L was ₹{DailyPL:N2}", _dailyPnL);
                        _dailyPnL = 0;
                        _lastPnLReset = DateTime.UtcNow.Date;
                        _dailyLossLimitHit = false;
                    }
                    finally
                    {
                        _tradeLock.Release();
                    }

                    // Clear today's trades
                    while (_todayTrades.TryTake(out _)) { }

                    _logger.LogInformation("Daily P&L reset completed");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("P&L reset cancelled during shutdown");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resetting daily P&L");
                    // Wait before retrying on error
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<bool> ShouldTrade(TradeItem trade, PortfolioSummary portfolio, List<Holding> holdings, IAngelOneService angelOneService)
        {
            try
            {
                // Use cached holdings instead of fetching again
                if (holdings.Any(h => string.Equals(h.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Already have position in {Symbol}, skipping", trade.Symbol);
                    return false;
                }

                var kellyFraction = CalculateKellyFraction(
                    trade.Confidence / 100m,
                    trade.RiskReward
                );

                var maxPositionValue = portfolio.CurrentValue * MAX_POSITION_SIZE_PERCENT;
                var kellyPositionValue = portfolio.CurrentValue * kellyFraction;
                var positionSize = Math.Min(kellyPositionValue, maxPositionValue);
                var quantity = (int)(positionSize / trade.CurrentPrice);

                if (quantity < 1)
                {
                    _logger.LogDebug("Position size too small for {Symbol}", trade.Symbol);
                    return false;
                }

                var totalRisk = portfolio.CurrentValue * MAX_PORTFOLIO_RISK;
                var tradeRisk = quantity * (trade.CurrentPrice - trade.StopLoss);

                if (tradeRisk > totalRisk)
                {
                    _logger.LogWarning("Trade risk {TradeRisk:C} exceeds portfolio limit {TotalRisk:C}",
                        tradeRisk, totalRisk);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ShouldTrade for {Symbol}", trade?.Symbol);
                return false;
            }
        }

        private async Task ExecuteTrade(TradeItem trade, PortfolioSummary portfolio, IAngelOneService angelOneService)
        {
            try
            {
                var kellyFraction = CalculateKellyFraction(
                    trade.Confidence / 100m,
                    trade.RiskReward
                );

                var positionValue = portfolio.CurrentValue * Math.Min(kellyFraction, MAX_POSITION_SIZE_PERCENT);
                var quantity = (int)(positionValue / trade.CurrentPrice);

                if (quantity < 1)
                {
                    _logger.LogDebug("Quantity too small for {Symbol}", trade.Symbol);
                    return;
                }

                _logger.LogInformation("Executing trade: {Symbol} {Quantity} @ ₹{Price:F2}",
                    trade.Symbol, quantity, trade.CurrentPrice);

                var order = new OrderRequest
                {
                    Symbol = trade.Symbol,
                    Action = "BUY",
                    Quantity = quantity,
                    Price = trade.CurrentPrice,
                    Exchange = trade.Category?.Contains("BSE") == true ? "BSE" : "NSE",
                    Variety = "NORMAL",
                    OrderType = "LIMIT",
                    ProductType = "DELIVERY",
                    Duration = "DAY"
                };

                var result = await angelOneService.PlaceOrderAsync(order);

                if (result.Status == "SUCCESS" || result.Status == "PLACED")
                {
                    await RecordTrade(trade, quantity, result.OrderId);
                    await PlaceStopLoss(trade.Symbol, quantity, trade.StopLoss, result.OrderId, angelOneService);

                    _logger.LogInformation("Trade executed successfully: Order {OrderId} for {Symbol}",
                        result.OrderId, trade.Symbol);
                }
                else
                {
                    _logger.LogError("Failed to execute trade for {Symbol}: {Message}",
                        trade.Symbol, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing trade for {Symbol}", trade.Symbol);
            }
        }

        private decimal CalculateKellyFraction(decimal winProb, decimal riskReward)
        {
            if (riskReward <= 0) return 0;

            var kelly = (winProb * (riskReward + 1) - 1) / riskReward;
            return Math.Max(0, Math.Min(kelly * 0.5m, MAX_POSITION_SIZE_PERCENT));
        }

        private async Task PlaceStopLoss(string symbol, int quantity, decimal stopLoss, string parentOrderId, IAngelOneService angelOneService)
        {
            try
            {
                _logger.LogInformation("Setting stop loss for {Symbol}: {Quantity} shares @ ₹{StopLoss:F2}",
                    symbol, quantity, stopLoss);

                if (stopLoss <= 0)
                {
                    _logger.LogWarning("Invalid stop loss price for {Symbol}: {StopLoss}", symbol, stopLoss);
                    return;
                }

                // In production, you would actually place the stop loss order
                _logger.LogInformation("Stop loss would be placed for {Symbol} at ₹{StopLoss:F2} (Parent Order: {ParentOrderId})",
                    symbol, stopLoss, parentOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing stop loss for {Symbol}", symbol);
            }
        }

        private async Task<bool> HasExceededDailyLoss(PortfolioSummary portfolio)
        {
            try
            {
                await _tradeLock.WaitAsync();
                try
                {
                    var dailyLossLimit = portfolio.CurrentValue * MAX_DAILY_LOSS_PERCENT;

                    if (_dailyPnL < -dailyLossLimit)
                    {
                        _logger.LogWarning("Daily loss limit exceeded. Daily P&L: ₹{DailyPL:N2}, Limit: ₹{Limit:N2}",
                            _dailyPnL, dailyLossLimit);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    _tradeLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking daily loss limit");
                return false;
            }
        }

        private async Task RecordTrade(TradeItem trade, int quantity, string orderId)
        {
            var execution = new TradeExecution
            {
                Symbol = trade.Symbol,
                Action = "BUY",
                Quantity = quantity,
                Price = trade.CurrentPrice,
                Time = DateTime.Now,
                OrderId = orderId,
                ProfitLoss = 0
            };

            _todayTrades.Add(execution);
            _activeTrades.TryAdd(orderId, execution);

            _logger.LogInformation("Trade recorded: {Symbol} {Quantity} @ ₹{Price:F2} - Order ID: {OrderId}",
                trade.Symbol, quantity, trade.CurrentPrice, orderId);

            await Task.CompletedTask;
        }

        public async Task UpdateTradeOnExit(string symbol, int quantity, decimal exitPrice, string orderId)
        {
            if (_activeTrades.TryGetValue(orderId, out var trade))
            {
                trade.ExitPrice = exitPrice;
                trade.ExitedAt = DateTime.Now;
                trade.ProfitLoss = (exitPrice - trade.Price) * trade.Quantity;

                await _tradeLock.WaitAsync();
                try
                {
                    _dailyPnL += trade.ProfitLoss;
                    _logger.LogInformation("Trade exited: {Symbol} {Quantity} @ ₹{ExitPrice:F2}, P&L: ₹{ProfitLoss:F2}, Daily P&L: ₹{DailyPL:F2}",
                        symbol, quantity, exitPrice, trade.ProfitLoss, _dailyPnL);
                }
                finally
                {
                    _tradeLock.Release();
                }
            }
        }

        private bool IsMarketOpen()
        {
            var now = DateTime.Now;

            if (now.DayOfWeek < DayOfWeek.Monday || now.DayOfWeek > DayOfWeek.Friday)
                return false;

            // Market hours: 9:15 AM to 3:30 PM
            var marketOpen = new DateTime(now.Year, now.Month, now.Day, 9, 15, 0);
            var marketClose = new DateTime(now.Year, now.Month, now.Day, 15, 30, 0);

            return now >= marketOpen && now <= marketClose;
        }

        private async Task WaitForMarketOpen(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Waiting for market to open at 9:15 AM...");

            while (!stoppingToken.IsCancellationRequested)
            {
                if (IsMarketOpen())
                {
                    _logger.LogInformation("Market is now open. Starting trading operations.");
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Auto-Trading Service is stopping...");

            try
            {
                await base.StopAsync(cancellationToken);
                _logger.LogInformation("Auto-Trading Service stopped successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Auto-Trading Service stop was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Auto-Trading Service");
            }
        }
    }

    public class TradeExecution
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public DateTime Time { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public decimal? ExitPrice { get; set; }
        public DateTime? ExitedAt { get; set; }
        public decimal ProfitLoss { get; set; }
        public DateTime ExecutedAt => Time;
    }
}