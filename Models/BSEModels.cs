using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region BSE API Models

    /// <summary>
    /// BSE quote API response
    /// </summary>
    public class BSEQuoteResponse
    {
        [JsonPropertyName("scrip_code")]
        public string? ScripCode { get; set; }

        [JsonPropertyName("company_name")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("current_price")]
        public decimal CurrentPrice { get; set; }

        [JsonPropertyName("change")]
        public decimal Change { get; set; }

        [JsonPropertyName("percentage_change")]
        public decimal PercentChange { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("day_high")]
        public decimal DayHigh { get; set; }

        [JsonPropertyName("day_low")]
        public decimal DayLow { get; set; }

        [JsonPropertyName("prev_close")]
        public decimal PreviousClose { get; set; }

        [JsonPropertyName("total_traded_volume")]
        public long Volume { get; set; }

        [JsonPropertyName("high_52")]
        public decimal YearHigh { get; set; }

        [JsonPropertyName("low_52")]
        public decimal YearLow { get; set; }

        [JsonPropertyName("pe")]
        public decimal? PE { get; set; }

        [JsonPropertyName("market_capitalisation")]
        public decimal MarketCap { get; set; }

        [JsonPropertyName("face_value")]
        public decimal FaceValue { get; set; }

        [JsonPropertyName("industry")]
        public string? Industry { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    #endregion
}
