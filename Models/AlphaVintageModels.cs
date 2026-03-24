using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region Alpha Vantage API Models

    /// <summary>
    /// Alpha Vantage API response model
    /// </summary>
    public class AlphaVantageResponse
    {
        [JsonPropertyName("Global Quote")]
        public Dictionary<string, GlobalQuote>? GlobalQuote { get; set; }
    }

    /// <summary>
    /// Alpha Vantage quote model with JSON property mapping
    /// </summary>
    public class GlobalQuote
    {
        [JsonPropertyName("01. symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("02. open")]
        public decimal Open { get; set; }

        [JsonPropertyName("03. high")]
        public decimal High { get; set; }

        [JsonPropertyName("04. low")]
        public decimal Low { get; set; }

        [JsonPropertyName("05. price")]
        public decimal Price { get; set; }

        [JsonPropertyName("06. volume")]
        public long Volume { get; set; }

        [JsonPropertyName("07. latest trading day")]
        public DateTime LatestTradingDay { get; set; }

        [JsonPropertyName("08. previous close")]
        public decimal PreviousClose { get; set; }

        [JsonPropertyName("09. change")]
        public decimal Change { get; set; }

        [JsonPropertyName("10. change percent")]
        public string ChangePercent { get; set; } = string.Empty;
    }

    #endregion
}
