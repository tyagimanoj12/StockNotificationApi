using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region Groww API Models

    /// <summary>
    /// Groww session information
    /// </summary>
    public class GrowwSession
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime Expiry { get; set; }
    }

    /// <summary>
    /// Groww authentication response
    /// </summary>
    public class GrowwAuthResponse
    {
        public string token { get; set; } = string.Empty;
        public DateTime expiry { get; set; }
        public string userId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Groww order response
    /// </summary>
    public class GrowwOrderResponse
    {
        public bool success { get; set; }
        public GrowwOrderData data { get; set; } = new();
        public string message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Groww order data
    /// </summary>
    public class GrowwOrderData
    {
        [JsonPropertyName("order_id")]
        public string order_id { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Groww quote
    /// </summary>
    public class GrowwQuote
    {
        public decimal ltp { get; set; }
        public decimal open { get; set; }
        public decimal high { get; set; }
        public decimal low { get; set; }
        public decimal close { get; set; }
        public long volume { get; set; }
        public decimal change { get; set; }
        public decimal changePercent { get; set; }
    }

    /// <summary>
    /// Groww positions response
    /// </summary>
    public class GrowwPositionsResponse
    {
        public bool success { get; set; }
        public List<GrowwPosition> data { get; set; } = new();
    }

    /// <summary>
    /// Groww position
    /// </summary>
    public class GrowwPosition
    {
        public string symbol { get; set; } = string.Empty;
        public int quantity { get; set; }
        public decimal average_price { get; set; }
        public decimal ltp { get; set; }
        public decimal pnl { get; set; }
        public string exchange { get; set; } = string.Empty;
    }

    #endregion
}
