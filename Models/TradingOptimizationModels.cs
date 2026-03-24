namespace StockNotificationApi.Models
{
    #region Trading Optimization Models

    /// <summary>
    /// Optimization plan for portfolio
    /// </summary>
    public class OptimizationPlan
    {
        public List<TradeAction> Actions { get; set; } = new();
        public decimal TotalImpact { get; set; }
        public decimal ExpectedProfit { get; set; }
        public int TradeCount => Actions.Count;
    }

    /// <summary>
    /// Individual trade action
    /// </summary>
    public class TradeAction
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // BUY, SELL, HOLD
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Priority { get; set; }
        public decimal ExpectedProfit { get; set; }
        public decimal RiskAmount { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TargetPrice { get; set; }
        public DateTime SuggestedTime { get; set; } = DateTime.Now;
        public string Category { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
    }

    /// <summary>
    /// Sell signal for holdings
    /// </summary>
    public class SellSignal
    {
        public bool ShouldSell { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Priority { get; set; }
        public decimal SuggestedPrice { get; set; }
    }

    #endregion
}
