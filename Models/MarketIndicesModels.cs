using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region Market Indices Models

    /// <summary>
    /// Market indices data
    /// </summary>
    public class MarketIndices
    {
        public IndexData Nifty50 { get; set; } = new();
        public IndexData Sensex { get; set; } = new();
        public IndexData BankNifty { get; set; } = new();
        public IndexData MidCap { get; set; } = new();
        public IndexData SmallCap { get; set; } = new();
    }

    /// <summary>
    /// Individual index data
    /// </summary>
    public class IndexData
    {
        public string Name { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }
        public decimal PreviousClose { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("high")]
        public decimal DayHigh { get; set; }

        [JsonPropertyName("low")]
        public decimal DayLow { get; set; }
    }

    #endregion
}
