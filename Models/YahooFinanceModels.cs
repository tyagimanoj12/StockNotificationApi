using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region Yahoo Finance API Models

    /// <summary>
    /// Yahoo Finance chart response
    /// </summary>
    public class YahooFinanceResponse
    {
        [JsonPropertyName("chart")]
        public YahooChart? Chart { get; set; }
    }

    /// <summary>
    /// Yahoo Finance chart container
    /// </summary>
    public class YahooChart
    {
        [JsonPropertyName("result")]
        public List<YahooResult>? Result { get; set; }
    }

    /// <summary>
    /// Yahoo Finance result container
    /// </summary>
    public class YahooResult
    {
        [JsonPropertyName("meta")]
        public YahooMeta? Meta { get; set; }
    }

    /// <summary>
    /// Yahoo Finance metadata
    /// </summary>
    public class YahooMeta
    {
        [JsonPropertyName("regularMarketPrice")]
        public decimal? RegularMarketPrice { get; set; }

        [JsonPropertyName("previousClose")]
        public decimal? PreviousClose { get; set; }

        [JsonPropertyName("regularMarketDayHigh")]
        public decimal? RegularMarketDayHigh { get; set; }

        [JsonPropertyName("regularMarketDayLow")]
        public decimal? RegularMarketDayLow { get; set; }

        [JsonPropertyName("regularMarketOpen")]
        public decimal? RegularMarketOpen { get; set; }

        [JsonPropertyName("regularMarketVolume")]
        public long? RegularMarketVolume { get; set; }
    }

    /// <summary>
    /// Yahoo Finance news response
    /// </summary>
    public class YahooNewsResponse
    {
        public List<YahooNewsItem>? news { get; set; }
    }

    /// <summary>
    /// Yahoo Finance news item
    /// </summary>
    public class YahooNewsItem
    {
        public string? title { get; set; }
        public string? summary { get; set; }
        public string? publisher { get; set; }
        public string? link { get; set; }

        [JsonPropertyName("providerPublishTime")]
        public long providerPublishTime { get; set; }
    }

    #endregion
}
