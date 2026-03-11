using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class HoldingOptimizer
    {
        private readonly IAngelOneService _angelOneService;
        private readonly ITradingService _tradingService;
        private readonly ILogger<HoldingOptimizer> _logger;

        public HoldingOptimizer(
            IAngelOneService angelOneService,
            ITradingService tradingService,
            ILogger<HoldingOptimizer> logger)
        {
            _angelOneService = angelOneService ?? throw new ArgumentNullException(nameof(angelOneService));
            _tradingService = tradingService ?? throw new ArgumentNullException(nameof(tradingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<OptimizationPlan> AnalyzeHoldingsAsync()
        {
            try
            {
                _logger.LogInformation("Starting holdings analysis...");

                var holdings = await _angelOneService.GetHoldingsAsync();
                if (holdings == null || !holdings.Any())
                {
                    _logger.LogInformation("No holdings found");
                    return new OptimizationPlan();
                }

                var todayTrades = await _tradingService.GetTodaysTradesAsync(60);
                if (todayTrades?.Trades == null)
                {
                    _logger.LogInformation("No trades available for today");
                    todayTrades = new TradingDashboard { Trades = new List<TradeItem>() };
                }

                var plan = new OptimizationPlan
                {
                    Actions = new List<TradeAction>()
                };

                foreach (var holding in holdings)
                {
                    if (holding == null) continue;

                    var currentTrade = todayTrades.Trades.FirstOrDefault(t =>
                        t != null && t.Symbol?.Equals(holding.Symbol, StringComparison.OrdinalIgnoreCase) == true);

                    if (currentTrade != null)
                    {
                        // Stock is in today's picks
                        var action = DetermineAction(holding, currentTrade);
                        if (action != null)
                        {
                            plan.Actions.Add(action);
                            _logger.LogDebug("Added action for {Symbol}: {Action}", holding.Symbol, action.Action);
                        }
                    }
                    else
                    {
                        // Stock not in today's picks - check if we should sell
                        var sellSignal = await ShouldSell(holding);
                        if (sellSignal.ShouldSell)
                        {
                            plan.Actions.Add(new TradeAction
                            {
                                Symbol = holding.Symbol,
                                Action = "SELL",
                                Quantity = holding.Quantity,
                                Price = holding.CurrentPrice,
                                Reason = sellSignal.Reason,
                                Priority = sellSignal.Priority,
                                StopLoss = holding.CurrentPrice * 0.95m, // Default stop loss
                                TargetPrice = holding.CurrentPrice * 1.05m, // Default target
                                ExpectedProfit = CalculateExpectedProfit(holding, sellSignal)
                            });
                            _logger.LogDebug("Added sell signal for {Symbol}: {Reason}", holding.Symbol, sellSignal.Reason);
                        }
                    }
                }

                plan.TotalImpact = plan.Actions.Sum(a => a.ExpectedProfit);
                _logger.LogInformation("Analysis complete. Found {Count} actions", plan.Actions.Count);

                return plan;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing holdings");
                return new OptimizationPlan();
            }
        }

        private TradeAction DetermineAction(Holding holding, TradeItem trade)
        {
            if (holding == null || trade == null)
                return null;

            var currentPrice = holding.CurrentPrice;
            var averagePrice = holding.AveragePrice > 0 ? holding.AveragePrice : currentPrice;
            var profitLoss = ((currentPrice - averagePrice) / averagePrice) * 100;

            // Target reached - sell all
            if (currentPrice >= trade.TargetPrice)
            {
                var expectedProfit = (currentPrice - averagePrice) * holding.Quantity;
                return new TradeAction
                {
                    Symbol = holding.Symbol,
                    Action = "SELL",
                    Quantity = holding.Quantity,
                    Price = currentPrice,
                    Reason = $"Target reached (Current: ₹{currentPrice:F2}, Target: ₹{trade.TargetPrice:F2})",
                    Priority = 1,
                    StopLoss = trade.StopLoss,
                    TargetPrice = trade.TargetPrice,
                    ExpectedProfit = expectedProfit
                };
            }

            // Stop loss hit - sell all
            if (currentPrice <= trade.StopLoss)
            {
                var expectedLoss = (averagePrice - currentPrice) * holding.Quantity;
                return new TradeAction
                {
                    Symbol = holding.Symbol,
                    Action = "SELL",
                    Quantity = holding.Quantity,
                    Price = currentPrice,
                    Reason = $"Stop loss hit (Current: ₹{currentPrice:F2}, SL: ₹{trade.StopLoss:F2})",
                    Priority = 1,
                    StopLoss = trade.StopLoss,
                    TargetPrice = trade.TargetPrice,
                    ExpectedProfit = -expectedLoss // Negative for loss
                };
            }

            // Book partial profits if gain > 10%
            if (profitLoss > 10)
            {
                var sellQuantity = (int)(holding.Quantity * 0.5m);
                var expectedProfit = ((currentPrice - averagePrice) * sellQuantity);

                return new TradeAction
                {
                    Symbol = holding.Symbol,
                    Action = "SELL",
                    Quantity = sellQuantity,
                    Price = currentPrice,
                    Reason = $"Book partial profits at {profitLoss:F1}% gain",
                    Priority = 2,
                    StopLoss = trade.StopLoss,
                    TargetPrice = trade.TargetPrice,
                    ExpectedProfit = expectedProfit
                };
            }

            // Hold
            return new TradeAction
            {
                Symbol = holding.Symbol,
                Action = "HOLD",
                Quantity = holding.Quantity,
                Price = currentPrice,
                Reason = $"Hold until target (₹{trade.TargetPrice:F2}) or stop loss (₹{trade.StopLoss:F2})",
                Priority = 3,
                StopLoss = trade.StopLoss,
                TargetPrice = trade.TargetPrice,
                ExpectedProfit = 0
            };
        }

        private async Task<SellSignal> ShouldSell(Holding holding)
        {
            if (holding == null)
                return new SellSignal { ShouldSell = false };

            var currentPrice = holding.CurrentPrice;
            var averagePrice = holding.AveragePrice > 0 ? holding.AveragePrice : currentPrice;
            var profitLoss = ((currentPrice - averagePrice) / averagePrice) * 100;

            // Check if stock is underperforming (loss > 15%)
            if (profitLoss < -15)
            {
                return new SellSignal
                {
                    ShouldSell = true,
                    Reason = $"Stop loss: {profitLoss:F1}% loss",
                    Priority = 1
                };
            }

            // Book profits if gain > 20%
            if (profitLoss > 20)
            {
                return new SellSignal
                {
                    ShouldSell = true,
                    Reason = $"Book profits: +{profitLoss:F1}% gain",
                    Priority = 2
                };
            }

            return new SellSignal { ShouldSell = false };
        }

        private decimal CalculateExpectedProfit(Holding holding, SellSignal signal)
        {
            if (holding == null) return 0;

            var averagePrice = holding.AveragePrice > 0 ? holding.AveragePrice : holding.CurrentPrice;

            if (signal.Reason.Contains("loss"))
            {
                // Expected loss
                return -((averagePrice - holding.CurrentPrice) * holding.Quantity);
            }
            else
            {
                // Expected profit
                return ((holding.CurrentPrice - averagePrice) * holding.Quantity);
            }
        }
    }
}