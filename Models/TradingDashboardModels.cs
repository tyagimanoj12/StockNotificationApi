using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region Trading Dashboard Models

    /// <summary>
    /// Trading dashboard with today's trading signals
    /// </summary>
    public class TradingDashboard
    {
        public string MarketPhase { get; set; } = string.Empty;
        public decimal MarketConfidence { get; set; }
        public List<TradeItem> Trades { get; set; } = new();
        public TradingSummary Summary { get; set; } = new();
    }

    /// <summary>
    /// Individual trade recommendation (used in dashboard)
    /// </summary>
    public class TradeItem
    {
        public string Symbol { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public decimal TargetPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Confidence { get; set; }
        public decimal RiskReward { get; set; }
        public decimal PotentialReturn { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    /// <summary>
    /// Buy recommendation with quantity
    /// </summary>
    public class BuyRecommendation
    {
        public TradeItem Trade { get; set; } = new();
        public int RecommendedQuantity { get; set; }
        public decimal TotalCost => RecommendedQuantity * Trade.CurrentPrice;
    }

    /// <summary>
    /// Trading summary statistics
    /// </summary>
    public class TradingSummary
    {
        public int TotalTrades { get; set; }
        public int HighConfidenceTrades { get; set; }
        public int MediumConfidenceTrades { get; set; }
        public decimal AverageConfidence { get; set; }
        public decimal BestRiskReward { get; set; }
        public decimal TotalPotentialReturn { get; set; }
    }

    /// <summary>
    /// Top trades response wrapper
    /// </summary>
    public class TopTradesResponse
    {
        public List<TopTradeItem> Trades { get; set; } = new();
    }

    /// <summary>
    /// Top trade item (camelCase for JSON serialization)
    /// </summary>
    public class TopTradeItem
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("currentPrice")]
        public decimal CurrentPrice { get; set; }

        [JsonPropertyName("targetPrice")]
        public decimal TargetPrice { get; set; }

        [JsonPropertyName("stopLoss")]
        public decimal StopLoss { get; set; }

        [JsonPropertyName("confidence")]
        public decimal Confidence { get; set; }

        [JsonPropertyName("riskReward")]
        public decimal RiskReward { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    #endregion    
}
