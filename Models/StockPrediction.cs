namespace StockNotificationApi.Models
{
    public class StockPrediction
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public string Prediction { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
        public string Confidence { get; set; } = string.Empty;
        public List<string> KeyFactors { get; set; } = new();
        public string ShortTermOutlook { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = string.Empty;
    }

    public class DailyPredictionReport
    {
        public DateTime Date { get; set; }
        public List<StockPrediction> Predictions { get; set; } = new();
        public string MarketSummary { get; set; } = string.Empty;
        public string TopPick { get; set; } = string.Empty;
    }
}