using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    #region Enhanced Prediction Models

    /// <summary>
    /// Enhanced market analysis with historical and future predictions
    /// </summary>
    public class EnhancedMarketAnalysis
    {
        public DateTime AnalysisDate { get; set; }
        public string MarketPhase { get; set; } = string.Empty;
        public string OverallSentiment { get; set; } = string.Empty;
        public decimal ConfidenceScore { get; set; }
        public HistoricalAnalysis Past10Days { get; set; } = new();
        public FuturePredictions Next10Days { get; set; } = new();
        public CategoryAnalysis LargeCap { get; set; } = new();
        public CategoryAnalysis MidCap { get; set; } = new();
        public CategoryAnalysis SmallCap { get; set; } = new();
        public List<NewsImpactAnalysis> KeyNewsImpacts { get; set; } = new();
        public TechnicalSummary TechnicalIndicators { get; set; } = new();
    }

    /// <summary>
    /// Historical market analysis
    /// </summary>
    public class HistoricalAnalysis
    {
        public List<DailyPerformance> DailyPerformances { get; set; } = new();
        public decimal AverageChange { get; set; }
        public decimal Volatility { get; set; }
        public string Trend { get; set; } = string.Empty;
        public List<string> KeyEvents { get; set; } = new();
        public Dictionary<string, decimal> SectorPerformance { get; set; } = new();
    }

    /// <summary>
    /// Future market predictions
    /// </summary>
    public class FuturePredictions
    {
        public List<DailyPrediction> DailyPredictions { get; set; } = new();
        public string OverallOutlook { get; set; } = string.Empty;
        public decimal ExpectedReturn { get; set; }
        public string RiskLevel { get; set; } = string.Empty;
        public List<string> KeyLevels { get; set; } = new();
        public List<OpportunityAlert> Opportunities { get; set; } = new();
    }

    /// <summary>
    /// Daily market performance
    /// </summary>
    public class DailyPerformance
    {
        public DateTime Date { get; set; }
        public decimal NiftyChange { get; set; }
        public decimal SensexChange { get; set; }
        public string MarketSentiment { get; set; } = string.Empty;
        public string KeyDriver { get; set; } = string.Empty;
    }

    /// <summary>
    /// Daily market prediction
    /// </summary>
    public class DailyPrediction
    {
        public DateTime Date { get; set; }
        public string PredictedTrend { get; set; } = string.Empty;
        public decimal PredictedRange { get; set; }
        public string KeyFactors { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
    }

    /// <summary>
    /// Category-wise analysis
    /// </summary>
    public class CategoryAnalysis
    {
        public string Category { get; set; } = string.Empty;
        public List<TopStockPick> TopPicks { get; set; } = new();
        public List<TopStockPick> AvoidStocks { get; set; } = new();
        public decimal CategoryMomentum { get; set; }
        public string Outlook { get; set; } = string.Empty;
        public Dictionary<string, decimal> TechnicalScore { get; set; } = new();
    }

    /// <summary>
    /// Top stock pick with entry/exit levels
    /// </summary>
    public class TopStockPick
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public decimal TargetPrice { get; set; }
        public decimal StopLoss { get; set; }
        public string Timeframe { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
        public List<string> TechnicalSignals { get; set; } = new();
        public List<string> NewsSentiment { get; set; } = new();
    }

    /// <summary>
    /// News impact analysis for enhanced market reports
    /// </summary>
    public class NewsImpactAnalysis
    {
        public string Headline { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
        public string Impact { get; set; } = string.Empty;
        public List<string> AffectedStocks { get; set; } = new();
        public List<string> AffectedSectors { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// Technical indicator summary
    /// </summary>
    public class TechnicalSummary
    {
        public string RSIStatus { get; set; } = string.Empty;
        public string MACDSignal { get; set; } = string.Empty;
        public string BollingerPosition { get; set; } = string.Empty;
        public Dictionary<string, string> MovingAverages { get; set; } = new();
        public string VolumeAnalysis { get; set; } = string.Empty;
    }

    /// <summary>
    /// Trading opportunity alert
    /// </summary>
    public class OpportunityAlert
    {
        public string Type { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public decimal Target { get; set; }
        public decimal StopLoss { get; set; }
        public string RiskReward { get; set; } = string.Empty;
    }

    #endregion
}
