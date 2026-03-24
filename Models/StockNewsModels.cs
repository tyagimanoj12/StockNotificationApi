using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    #region Stock News Models

    /// <summary>
    /// Stock-related news article with enhanced properties for better display
    /// </summary>
    public class StockNews
    {
        /// <summary>
        /// Primary stock symbol associated with the news (can be "Market" for general news)
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Full news headline - NEVER truncated
        /// </summary>
        [Required]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Brief summary or description of the news
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// News source (e.g., NDTV, Business Standard, Economic Times)
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// When the news was published
        /// </summary>
        public DateTime PublishedAt { get; set; }

        /// <summary>
        /// URL to the full article
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Sentiment: Positive, Negative, Neutral
        /// </summary>
        public string Sentiment { get; set; } = string.Empty;

        /// <summary>
        /// Numeric sentiment score between -1 and 1
        /// </summary>
        [Range(-1, 1)]
        public decimal? SentimentScore { get; set; }

        /// <summary>
        /// List of stock symbols mentioned in or affected by this news
        /// </summary>
        public List<string> AffectedStocks { get; set; } = new();

        /// <summary>
        /// Impact level: "🔴 HIGH IMPACT", "🟢 POSITIVE", "🟡 MEDIUM IMPACT", "⚪ GENERAL"
        /// </summary>
        public string Impact { get; set; } = string.Empty;

        /// <summary>
        /// Economic sectors affected by this news
        /// </summary>
        public List<string> AffectedSectors { get; set; } = new();

        /// <summary>
        /// Whether this is breaking news
        /// </summary>
        public bool IsBreaking { get; set; }

        /// <summary>
        /// News category: Market, Company, Sector, Economy, Global
        /// </summary>
        public string Category { get; set; } = string.Empty;
    }

    /// <summary>
    /// Daily market briefing with predictions and news
    /// </summary>
    public class DailyBriefing
    {
        public DateTime Date { get; set; }
        public string MarketSummary { get; set; } = string.Empty;
        public List<StockPrediction> LargeCapPicks { get; set; } = new();
        public List<StockPrediction> MidCapPicks { get; set; } = new();
        public List<StockPrediction> SmallCapPicks { get; set; } = new();
        public List<StockNews> TopNews { get; set; } = new();
        public Dictionary<string, string> SectorPerformance { get; set; } = new();
        public MarketIndices Indices { get; set; } = new();
        public string TopPick { get; set; } = string.Empty;
    }

    /// <summary>
    /// Stock prediction model
    /// </summary>
    //public class StockPrediction
    //{
    //    public string Symbol { get; set; } = string.Empty;
    //    public string CompanyName { get; set; } = string.Empty;
    //    public decimal CurrentPrice { get; set; }
    //    public decimal TargetPrice { get; set; }
    //    public decimal StopLoss { get; set; }
    //    public string Reason { get; set; } = string.Empty;
    //    public decimal Confidence { get; set; }
    //}

    #endregion
}
