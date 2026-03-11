using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class AutoTradingService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoTradingService> _logger;
        private readonly IConfiguration _configuration;

        // Risk management constants
        private const decimal MAX_POSITION_SIZE_PERCENT = 0.05m;
        private const decimal MAX_DAILY_LOSS_PERCENT = 0.02m;
        private const decimal MAX_PORTFOLIO_RISK = 0.10m;
        private const int MAX_TRADES_PER_DAY = 5;

        // In-memory trade tracking
        private static readonly List<TradeExecution> _todayTrades = new();
        private static readonly object _tradeLock = new();

        public AutoTradingService(
            IServiceProvider serviceProvider,
            ILogger<AutoTradingService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Auto-Trading Service Started");

            // Check if auto-trading is enabled in configuration
            var autoTradingEnabled = _configuration.GetValue<bool>("Trading:AutoTradingEnabled", false);
            if (!autoTradingEnabled)
            {
                _logger.LogInformation("Auto-trading is disabled. Set Trading:AutoTradingEnabled=true to enable.");
                return;
            }

            // Wait for market open (9:15 AM)
            await WaitForMarketOpen(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check if market is open
                    if (!IsMarketOpen())
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                        continue;
                    }

                    // Create a scope for scoped services
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var tradingService = scope.ServiceProvider.GetRequiredService<ITradingService>();
                        var portfolioService = scope.ServiceProvider.GetRequiredService<IPortfolioService>();
                        var angelOneService = scope.ServiceProvider.GetRequiredService<IAngelOneService>();
                        var growwService = scope.ServiceProvider.GetService<IGrowwService>();

                        // Get today's trading signals
                        var dashboard = await tradingService.GetTodaysTradesAsync(minConfidence: 70);

                        if (dashboard?.Trades == null || !dashboard.Trades.Any())
                        {
                            _logger.LogInformation("No trading signals available today");
                            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                            continue;
                        }

                        // Get current portfolio
                        var portfolio = await GetCombinedPortfolioValue(angelOneService);

                        // Check daily loss limit
                        if (await HasExceededDailyLoss(portfolio))
                        {
                            _logger.LogWarning("Daily loss limit exceeded. Stopping trading for today.");
                            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                            continue;
                        }

                        // Check trade count limit
                        var todayTradeCount = GetTodayTradeCount();
                        if (todayTradeCount >= MAX_TRADES_PER_DAY)
                        {
                            _logger.LogInformation("Maximum trades per day ({Max}) reached. Stopping trading for today.", MAX_TRADES_PER_DAY);
                            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                            continue;
                        }

                        // Process each trading signal (limit to remaining trades)
                        var remainingTrades = MAX_TRADES_PER_DAY - todayTradeCount;
                        foreach (var trade in dashboard.Trades.OrderByDescending(t => t.RiskReward).Take(remainingTrades))
                        {
                            if (await ShouldTrade(trade, portfolio, angelOneService))
                            {
                                await ExecuteTrade(trade, portfolio, angelOneService);
                            }
                        }
                    }

                    // Wait before next evaluation (every 15 minutes during market hours)
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in auto-trading loop");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
        }

        private int GetTodayTradeCount()
        {
            lock (_tradeLock)
            {
                return _todayTrades.Count(t => t.ExecutedAt.Date == DateTime.UtcNow.Date);
            }
        }

        private async Task<bool> ShouldTrade(TradeItem trade, PortfolioSummary portfolio, IAngelOneService angelOneService)
        {
            try
            {
                // Check if we already have this position
                if (await HasPosition(trade.Symbol, angelOneService))
                {
                    _logger.LogInformation("Already have position in {Symbol}, skipping", trade.Symbol);
                    return false;
                }

                // Calculate position size based on Kelly Criterion
                var kellyFraction = CalculateKellyFraction(
                    trade.Confidence / 100m,
                    trade.RiskReward
                );

                // Apply position size limits
                var maxPositionValue = portfolio.CurrentValue * MAX_POSITION_SIZE_PERCENT;
                var kellyPositionValue = portfolio.CurrentValue * kellyFraction;

                var positionSize = Math.Min(kellyPositionValue, maxPositionValue);

                // Check if position is viable (minimum trade size)
                var quantity = (int)(positionSize / trade.CurrentPrice);
                if (quantity < 1)
                {
                    _logger.LogDebug("Position size too small for {Symbol}", trade.Symbol);
                    return false;
                }

                // Check total portfolio risk
                var totalRisk = portfolio.CurrentValue * MAX_PORTFOLIO_RISK;
                var tradeRisk = quantity * (trade.CurrentPrice - trade.StopLoss);

                if (tradeRisk > totalRisk)
                {
                    _logger.LogWarning("Trade risk {TradeRisk} exceeds portfolio limit {TotalRisk}",
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
                // Calculate position size
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

                // Place order on primary broker (Angel One)
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
                    // Record the trade
                    await RecordTrade(trade, quantity, result.OrderId);

                    // Set stop loss
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

            // Conservative Kelly (use half-Kelly for safety)
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

                var stopLossOrder = new OrderRequest
                {
                    Symbol = symbol,
                    Action = "SELL",
                    Quantity = quantity,
                    Price = stopLoss,
                    Exchange = "NSE",
                    Variety = "STOPLOSS",
                    OrderType = "SL",
                    ProductType = "DELIVERY",
                    Duration = "DAY"
                };

                // Uncomment when ready to place actual stop-loss orders
                // var result = await angelOneService.PlaceOrderAsync(stopLossOrder);
                // if (result.Status == "SUCCESS")
                // {
                //     _logger.LogInformation("Stop loss placed successfully for {Symbol}", symbol);
                // }

                _logger.LogInformation("Stop loss placement simulated for {Symbol} at ₹{StopLoss:F2} (Parent Order: {ParentOrderId})",
                    symbol, stopLoss, parentOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing stop loss for {Symbol}", symbol);
            }
        }

        private async Task<PortfolioSummary> GetCombinedPortfolioValue(IAngelOneService angelOneService)
        {
            try
            {
                var holdings = await angelOneService.GetHoldingsAsync();

                if (holdings == null || !holdings.Any())
                {
                    return new PortfolioSummary
                    {
                        CurrentValue = 0,
                        TotalInvestment = 0,
                        Holdings = new List<Holding>()
                    };
                }

                var currentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice);
                var totalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice);

                _logger.LogInformation("Portfolio Value: ₹{CurrentValue:N2}, Investment: ₹{TotalInvestment:N2}, P&L: ₹{ProfitLoss:N2} ({Percent:F1}%)",
                    currentValue, totalInvestment, currentValue - totalInvestment,
                    totalInvestment > 0 ? ((currentValue - totalInvestment) / totalInvestment) * 100 : 0);

                return new PortfolioSummary
                {
                    CurrentValue = currentValue,
                    TotalInvestment = totalInvestment,
                    Holdings = holdings,
                    AsOfDate = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting combined portfolio value");
                return new PortfolioSummary
                {
                    CurrentValue = 0,
                    TotalInvestment = 0,
                    Holdings = new List<Holding>()
                };
            }
        }

        private async Task<bool> HasPosition(string symbol, IAngelOneService angelOneService)
        {
            try
            {
                var holdings = await angelOneService.GetHoldingsAsync();
                return holdings.Any(h => string.Equals(h.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking position for {Symbol}", symbol);
                return false;
            }
        }

        private async Task<bool> HasExceededDailyLoss(PortfolioSummary portfolio)
        {
            try
            {
                var dailyPL = await GetDailyProfitLoss();
                var dailyLossLimit = portfolio.CurrentValue * MAX_DAILY_LOSS_PERCENT;

                if (dailyPL < -dailyLossLimit)
                {
                    _logger.LogWarning("Daily loss limit exceeded. Daily P&L: ₹{DailyPL:N2}, Limit: ₹{Limit:N2}",
                        dailyPL, dailyLossLimit);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking daily loss limit");
                return false;
            }
        }

        private async Task<decimal> GetDailyProfitLoss()
        {
            lock (_tradeLock)
            {
                var today = DateTime.UtcNow.Date;
                return _todayTrades
                    .Where(t => t.ExecutedAt.Date == today)
                    .Sum(t => t.ProfitLoss);
            }
        }

        private async Task RecordTrade(TradeItem trade, int quantity, string orderId)
        {
            lock (_tradeLock)
            {
                _todayTrades.Add(new TradeExecution
                {
                    Symbol = trade.Symbol,
                    Action = "BUY",
                    Quantity = quantity,
                    Price = trade.CurrentPrice,
                    Time = DateTime.Now,
                    OrderId = orderId,
                    ProfitLoss = 0
                });

                _logger.LogInformation("Trade recorded: {Symbol} {Quantity} @ ₹{Price:F2} - Order ID: {OrderId}",
                    trade.Symbol, quantity, trade.CurrentPrice, orderId);
            }

            await Task.CompletedTask;
        }

        public void UpdateTradeOnExit(string symbol, int quantity, decimal exitPrice)
        {
            lock (_tradeLock)
            {
                var trades = _todayTrades
                    .Where(t => t.Symbol == symbol && t.Action == "BUY" && t.ExitPrice == null)
                    .OrderBy(t => t.Time)
                    .Take(quantity)
                    .ToList();

                foreach (var trade in trades)
                {
                    trade.ExitPrice = exitPrice;
                    trade.ExitedAt = DateTime.Now;
                    trade.ProfitLoss = (exitPrice - trade.Price) * trade.Quantity;

                    _logger.LogInformation("Trade exited: {Symbol} {Quantity} @ ₹{ExitPrice:F2}, P&L: ₹{ProfitLoss:F2}",
                        trade.Symbol, trade.Quantity, exitPrice, trade.ProfitLoss);
                }
            }
        }

        private bool IsMarketOpen()
        {
            var now = DateTime.Now;

            if (now.DayOfWeek < DayOfWeek.Monday || now.DayOfWeek > DayOfWeek.Friday)
                return false;

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

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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