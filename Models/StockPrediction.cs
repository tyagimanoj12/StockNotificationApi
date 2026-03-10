using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    /// <summary>
    /// Represents an AI-generated prediction for a specific stock
    /// </summary>
    public class StockPrediction
    {
        /// <summary>
        /// Stock symbol (e.g., RELIANCE, TCS)
        /// </summary>
        [Required(ErrorMessage = "Symbol is required")]
        [StringLength(20, MinimumLength = 1, ErrorMessage = "Symbol must be between 1 and 20 characters")]
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Full company name
        /// </summary>
        [Required]
        [StringLength(100, ErrorMessage = "Company name cannot exceed 100 characters")]
        public string CompanyName { get; set; } = string.Empty;

        /// <summary>
        /// Current market price of the stock
        /// </summary>
        [Range(0.01, double.MaxValue, ErrorMessage = "Current price must be greater than 0")]
        public decimal CurrentPrice { get; set; }

        /// <summary>
        /// Price direction prediction (Bullish/Bearish/Neutral)
        /// </summary>
        [Required]
        [RegularExpression("^(Bullish|Bearish|Neutral)$", ErrorMessage = "Prediction must be Bullish, Bearish, or Neutral")]
        public string Prediction { get; set; } = string.Empty;

        /// <summary>
        /// Trading recommendation (Buy/Hold/Sell)
        /// </summary>
        [Required]
        [RegularExpression("^(Buy|Hold|Sell)$", ErrorMessage = "Recommendation must be Buy, Hold, or Sell")]
        public string Recommendation { get; set; } = string.Empty;

        /// <summary>
        /// Confidence level in the prediction (High/Medium/Low)
        /// </summary>
        [Required]
        [RegularExpression("^(High|Medium|Low)$", ErrorMessage = "Confidence must be High, Medium, or Low")]
        public string Confidence { get; set; } = string.Empty;

        /// <summary>
        /// Key factors influencing the prediction
        /// </summary>
        [MinLength(1, ErrorMessage = "At least one key factor is required")]
        public List<string> KeyFactors { get; set; } = new();

        /// <summary>
        /// Short-term outlook for the next 1-2 weeks
        /// </summary>
        [Required]
        [StringLength(200, ErrorMessage = "Short-term outlook cannot exceed 200 characters")]
        public string ShortTermOutlook { get; set; } = string.Empty;

        /// <summary>
        /// Risk level associated with the stock (High/Medium/Low)
        /// </summary>
        [Required]
        [RegularExpression("^(High|Medium|Low)$", ErrorMessage = "Risk level must be High, Medium, or Low")]
        public string RiskLevel { get; set; } = string.Empty;

        /// <summary>
        /// Returns a numeric confidence score (3=High, 2=Medium, 1=Low, 0=Unknown)
        /// </summary>
        public int GetConfidenceScore()
        {
            return Confidence?.ToLower() switch
            {
                "high" => 3,
                "medium" => 2,
                "low" => 1,
                _ => 0
            };
        }

        /// <summary>
        /// Returns whether this is a buy recommendation
        /// </summary>
        public bool IsBuyRecommendation => Recommendation?.Equals("Buy", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Returns whether this is a sell recommendation
        /// </summary>
        public bool IsSellRecommendation => Recommendation?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Returns whether this is a hold recommendation
        /// </summary>
        public bool IsHoldRecommendation => Recommendation?.Equals("Hold", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Returns whether the prediction is bullish
        /// </summary>
        public bool IsBullish => Prediction?.Equals("Bullish", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Returns whether the prediction is bearish
        /// </summary>
        public bool IsBearish => Prediction?.Equals("Bearish", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Returns whether the prediction is neutral
        /// </summary>
        public bool IsNeutral => Prediction?.Equals("Neutral", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Complete daily prediction report containing multiple stock predictions
    /// </summary>
    public class DailyPredictionReport
    {
        /// <summary>
        /// Date when the predictions were generated
        /// </summary>
        [Required]
        public DateTime Date { get; set; } = DateTime.Now;

        /// <summary>
        /// List of individual stock predictions
        /// </summary>
        [MinLength(1, ErrorMessage = "At least one prediction is required")]
        public List<StockPrediction> Predictions { get; set; } = new();

        /// <summary>
        /// Overall market summary and analysis
        /// </summary>
        [Required]
        [StringLength(1000, ErrorMessage = "Market summary cannot exceed 1000 characters")]
        public string MarketSummary { get; set; } = string.Empty;

        /// <summary>
        /// Top pick stock symbol for the day
        /// </summary>
        [Required]
        [StringLength(20, MinimumLength = 1, ErrorMessage = "Top pick symbol must be between 1 and 20 characters")]
        public string TopPick { get; set; } = string.Empty;

        /// <summary>
        /// Gets the number of buy recommendations in this report
        /// </summary>
        public int BuyCount => Predictions?.Count(p => p.IsBuyRecommendation) ?? 0;

        /// <summary>
        /// Gets the number of sell recommendations in this report
        /// </summary>
        public int SellCount => Predictions?.Count(p => p.IsSellRecommendation) ?? 0;

        /// <summary>
        /// Gets the number of hold recommendations in this report
        /// </summary>
        public int HoldCount => Predictions?.Count(p => p.IsHoldRecommendation) ?? 0;

        /// <summary>
        /// Gets the number of high confidence predictions
        /// </summary>
        public int HighConfidenceCount => Predictions?.Count(p => p.Confidence?.Equals("High", StringComparison.OrdinalIgnoreCase) == true) ?? 0;

        /// <summary>
        /// Gets the number of medium confidence predictions
        /// </summary>
        public int MediumConfidenceCount => Predictions?.Count(p => p.Confidence?.Equals("Medium", StringComparison.OrdinalIgnoreCase) == true) ?? 0;

        /// <summary>
        /// Gets the number of low confidence predictions
        /// </summary>
        public int LowConfidenceCount => Predictions?.Count(p => p.Confidence?.Equals("Low", StringComparison.OrdinalIgnoreCase) == true) ?? 0;

        /// <summary>
        /// Returns the top pick prediction object, or null if not found
        /// </summary>
        public StockPrediction? GetTopPickPrediction()
        {
            return Predictions?.FirstOrDefault(p => p.Symbol?.Equals(TopPick, StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>
        /// Returns all buy recommendations
        /// </summary>
        public IEnumerable<StockPrediction> GetBuyRecommendations()
        {
            return Predictions?.Where(p => p.IsBuyRecommendation) ?? Enumerable.Empty<StockPrediction>();
        }

        /// <summary>
        /// Returns all sell recommendations
        /// </summary>
        public IEnumerable<StockPrediction> GetSellRecommendations()
        {
            return Predictions?.Where(p => p.IsSellRecommendation) ?? Enumerable.Empty<StockPrediction>();
        }

        /// <summary>
        /// Returns all hold recommendations
        /// </summary>
        public IEnumerable<StockPrediction> GetHoldRecommendations()
        {
            return Predictions?.Where(p => p.IsHoldRecommendation) ?? Enumerable.Empty<StockPrediction>();
        }

        /// <summary>
        /// Returns predictions sorted by confidence (High to Low)
        /// </summary>
        public IEnumerable<StockPrediction> GetPredictionsByConfidence()
        {
            return Predictions?
                .OrderByDescending(p => p.GetConfidenceScore())
                .ThenBy(p => p.Symbol) ?? Enumerable.Empty<StockPrediction>();
        }
    }
}