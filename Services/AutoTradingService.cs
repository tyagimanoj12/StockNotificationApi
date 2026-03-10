using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    // Services/AutoTradingService.cs
    public class AutoTradingService : BackgroundService
    {
        private readonly ITradingService _tradingService;
        private readonly IPortfolioService _portfolioService;
        private readonly IAngelOneService _angelOneService;
        private readonly IGrowwService _growwService;
        private readonly ILogger<AutoTradingService> _logger;
        private readonly IConfiguration _configuration;

        // Risk management constants
        private const decimal MAX_POSITION_SIZE_PERCENT = 0.05m; // 5% max per position
        private const decimal MAX_DAILY_LOSS_PERCENT = 0.02m; // 2% max daily loss
        private const decimal MAX_PORTFOLIO_RISK = 0.10m; // 10% max portfolio risk
        private const int MAX_TRADES_PER_DAY = 5;

        public AutoTradingService(
            ITradingService tradingService,
            IPortfolioService portfolioService,
            IAngelOneService angelOneService,
            IGrowwService growwService,
            ILogger<AutoTradingService> logger,
            IConfiguration configuration)
        {
            _tradingService = tradingService ?? throw new ArgumentNullException(nameof(tradingService));
            _portfolioService = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
            _angelOneService = angelOneService ?? throw new ArgumentNullException(nameof(angelOneService));
            _growwService = growwService ?? throw new ArgumentNullException(nameof(growwService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Auto-Trading Service Started");

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

                    // Get today's trading signals
                    var dashboard = await _tradingService.GetTodaysTradesAsync(minConfidence: 70);

                    if (dashboard?.Trades == null || !dashboard.Trades.Any())
                    {
                        _logger.LogInformation("No trading signals available today");
                        await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                        continue;
                    }

                    // Get current portfolio
                    var portfolio = await GetCombinedPortfolioValue();

                    // Check daily loss limit
                    if (await HasExceededDailyLoss(portfolio))
                    {
                        _logger.LogWarning("Daily loss limit exceeded. Stopping trading for today.");
                        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                        continue;
                    }

                    // Process each trading signal
                    foreach (var trade in dashboard.Trades.OrderByDescending(t => t.RiskReward))
                    {
                        if (await ShouldTrade(trade, portfolio))
                        {
                            await ExecuteTrade(trade, portfolio);
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

        private async Task<bool> ShouldTrade(TradeItem trade, PortfolioSummary portfolio)
        {
            try
            {
                // Check if we already have this position
                if (await HasPosition(trade.Symbol))
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

        private async Task ExecuteTrade(TradeItem trade, PortfolioSummary portfolio)
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

                var result = await _angelOneService.PlaceOrderAsync(order);

                if (result.Status == "SUCCESS" || result.Status == "PLACED")
                {
                    // Record the trade
                    await RecordTrade(trade, quantity, result.OrderId);

                    // Set stop loss
                    await PlaceStopLoss(trade.Symbol, quantity, trade.StopLoss, result.OrderId);

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
            // Kelly Criterion: f = (p(b+1) - 1) / b
            // where p = win probability, b = risk/reward ratio
            if (riskReward <= 0) return 0;

            var kelly = (winProb * (riskReward + 1) - 1) / riskReward;

            // Conservative Kelly (use half-Kelly for safety)
            return Math.Max(0, Math.Min(kelly * 0.5m, MAX_POSITION_SIZE_PERCENT));
        }

        private async Task PlaceStopLoss(string symbol, int quantity, decimal stopLoss, string parentOrderId)
        {
            try
            {
                _logger.LogInformation("Setting stop loss for {Symbol}: {Quantity} shares @ ₹{StopLoss:F2}",
                    symbol, quantity, stopLoss);

                // In production, you'd place a proper stop-loss order
                // This could be a GTT (Good Till Triggered) order or bracket order

                // Example of placing a stop-loss order
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
                // var result = await _angelOneService.PlaceOrderAsync(stopLossOrder);

                _logger.LogInformation("Stop loss placement simulated for {Symbol} at ₹{StopLoss:F2}",
                    symbol, stopLoss);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing stop loss for {Symbol}", symbol);
            }
        }

        private async Task<PortfolioSummary> GetCombinedPortfolioValue()
        {
            try
            {
                var holdings = await _angelOneService.GetHoldingsAsync();

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

                // Don't assign TotalProfitLoss - it's calculated automatically

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

        private async Task<bool> HasPosition(string symbol)
        {
            try
            {
                var holdings = await _angelOneService.GetHoldingsAsync();
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
                // Track daily P&L
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
            // In production, track this in database
            // For now, return 0 as placeholder
            return await Task.FromResult(0m);
        }

        private async Task RecordTrade(TradeItem trade, int quantity, string orderId)
        {
            try
            {
                // In production, save to database
                _logger.LogInformation("Trade recorded: {Symbol} {Quantity} @ ₹{Price:F2} - Order ID: {OrderId}",
                    trade.Symbol, quantity, trade.CurrentPrice, orderId);

                // TODO: Save to database
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording trade for {Symbol}", trade.Symbol);
            }
        }

        private bool IsMarketOpen()
        {
            var now = DateTime.Now;

            // Check if it's a weekday (Monday to Friday)
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

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}