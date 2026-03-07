namespace StockNotificationApi.Models
{
    public class StockData
    {
        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }
        public decimal DayHigh { get; set; }
        public decimal DayLow { get; set; }
        public long Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public string Exchange { get; set; } = "BSE";
    }

    public class AlphaVantageResponse
    {
        public Dictionary<string, GlobalQuote> GlobalQuote { get; set; } = new();
    }

    public class GlobalQuote
    {
        [Newtonsoft.Json.JsonProperty("01. symbol")]
        public string Symbol { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonProperty("02. open")]
        public decimal Open { get; set; }

        [Newtonsoft.Json.JsonProperty("03. high")]
        public decimal High { get; set; }

        [Newtonsoft.Json.JsonProperty("04. low")]
        public decimal Low { get; set; }

        [Newtonsoft.Json.JsonProperty("05. price")]
        public decimal Price { get; set; }

        [Newtonsoft.Json.JsonProperty("06. volume")]
        public long Volume { get; set; }

        [Newtonsoft.Json.JsonProperty("07. latest trading day")]
        public DateTime LatestTradingDay { get; set; }

        [Newtonsoft.Json.JsonProperty("08. previous close")]
        public decimal PreviousClose { get; set; }

        [Newtonsoft.Json.JsonProperty("09. change")]
        public decimal Change { get; set; }

        [Newtonsoft.Json.JsonProperty("10. change percent")]
        public string ChangePercent { get; set; } = string.Empty;
    }
}