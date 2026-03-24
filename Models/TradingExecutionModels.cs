namespace StockNotificationApi.Models
{
    #region Trading Execution Models

    /// <summary>
    /// Order request
    /// </summary>
    public class OrderRequest
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // "BUY" or "SELL"
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Exchange { get; set; } = "NSE";
        public string Variety { get; set; } = "NORMAL";
        public string OrderType { get; set; } = "LIMIT";
        public string ProductType { get; set; } = "DELIVERY";
        public string Duration { get; set; } = "DAY";
    }

    /// <summary>
    /// Order response
    /// </summary>
    public class OrderResponse
    {
        public string OrderId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime OrderTime { get; set; }
        public decimal ExecutedPrice { get; set; }
        public int ExecutedQuantity { get; set; }
    }

    /// <summary>
    /// Position information
    /// </summary>
    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal PnL => (CurrentPrice - BuyPrice) * Quantity;
        public decimal PnLPercent => BuyPrice > 0 ? ((CurrentPrice - BuyPrice) / BuyPrice) * 100 : 0;
    }

    /// <summary>
    /// Execution result for trades
    /// </summary>
    public class ExecutionResult
    {
        public bool Success { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal ExecutedPrice { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime ExecutionTime { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Daily profit and loss
    /// </summary>
    public class DailyPnL
    {
        public DateTime Date { get; set; }
        public decimal RealizedPnL { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
        public List<TradeExecution> Trades { get; set; } = new();
    }

    /// <summary>
    /// Trade execution details
    /// </summary>
    public class TradeExecution
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public DateTime Time { get; set; }
        public decimal ProfitLoss { get; set; }
        public string OrderId { get; set; } = string.Empty;
    }

    #endregion
}
