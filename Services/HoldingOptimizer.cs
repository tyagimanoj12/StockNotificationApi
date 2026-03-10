using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    // Services/HoldingOptimizer.cs
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
            _angelOneService = angelOneService;
            _tradingService = tradingService;
            _logger = logger;
        }

        public async Task<OptimizationPlan> AnalyzeHoldingsAsync()
        {
            var holdings = await _angelOneService.GetHoldingsAsync();
            var todayTrades = await _tradingService.GetTodaysTradesAsync(60);

            var plan = new OptimizationPlan();

            foreach (var holding in holdings)
            {
                var currentTrade = todayTrades.Trades.FirstOrDefault(t => t.Symbol == holding.Symbol);

                if (currentTrade != null)
                {
                    // Stock is in today's picks
                    var currentPrice = holding.CurrentPrice;
                    var targetPrice = currentTrade.TargetPrice;
                    var stopLoss = currentTrade.StopLoss;

                    // Calculate optimal action
                    var action = DetermineAction(holding, currentTrade);
                    plan.Actions.Add(action);
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
                            Reason = sellSignal.Reason,
                            Priority = sellSignal.Priority
                        });
                    }
                }
            }

            return plan;
        }

        private TradeAction DetermineAction(Holding holding, TradeItem trade)
        {
            var currentPrice = holding.CurrentPrice;
            var profitLoss = ((currentPrice - holding.AveragePrice) / holding.AveragePrice) * 100;

            if (currentPrice >= trade.TargetPrice)
            {
                return new TradeAction
                {
                    Symbol = holding.Symbol,
                    Action = "SELL",
                    Quantity = holding.Quantity,
                    Reason = $"Target reached (Current: ₹{currentPrice}, Target: ₹{trade.TargetPrice})",
                    Priority = 1
                };
            }

            if (currentPrice <= trade.StopLoss)
            {
                return new TradeAction
                {
                    Symbol = holding.Symbol,
                    Action = "SELL",
                    Quantity = holding.Quantity,
                    Reason = $"Stop loss hit (Current: ₹{currentPrice}, SL: ₹{trade.StopLoss})",
                    Priority = 1
                };
            }

            if (profitLoss > 10)
            {
                return new TradeAction
                {
                    Symbol = holding.Symbol,
                    Action = "SELL",
                    Quantity = (int)(holding.Quantity * 0.5m), // Book partial profits
                    Reason = $"Book partial profits at {profitLoss:F1}% gain",
                    Priority = 2
                };
            }

            return new TradeAction
            {
                Symbol = holding.Symbol,
                Action = "HOLD",
                Quantity = holding.Quantity,
                Reason = $"Hold until target (₹{trade.TargetPrice}) or stop loss (₹{trade.StopLoss})",
                Priority = 3
            };
        }

        private async Task<SellSignal> ShouldSell(Holding holding)
        {
            var currentPrice = holding.CurrentPrice;
            var profitLoss = ((currentPrice - holding.AveragePrice) / holding.AveragePrice) * 100;

            // Check if stock is underperforming
            if (profitLoss < -15)
            {
                return new SellSignal
                {
                    ShouldSell = true,
                    Reason = $"Stop loss: -{Math.Abs(profitLoss):F1}% loss",
                    Priority = 1
                };
            }

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
    }    
}
