using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    #region Core Stock Models

    /// <summary>
    /// Represents real-time stock data from various sources
    /// </summary>
    public class StockData
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        [Required]
        public string Name { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }

        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }

        [Range(0, double.MaxValue)]
        public decimal DayHigh { get; set; }

        [Range(0, double.MaxValue)]
        public decimal DayLow { get; set; }

        [Range(0, long.MaxValue)]
        public long Volume { get; set; }

        public DateTime Timestamp { get; set; }

        [StringLength(12)]
        public string? Isin { get; set; }

        public string Exchange { get; set; } = "BSE";

        [Range(0, double.MaxValue)]
        public decimal YearHigh { get; set; }

        [Range(0, double.MaxValue)]
        public decimal YearLow { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Open { get; set; }

        [Range(0, double.MaxValue)]
        public decimal PreviousClose { get; set; }

        [Range(0, 1000)]
        public decimal? PE { get; set; }

        [Range(0, double.MaxValue)]
        public decimal MarketCap { get; set; }

        [Range(0, 1000)]
        public decimal FaceValue { get; set; }

        public string Sector { get; set; } = string.Empty;
        public string Industry { get; set; } = string.Empty;
    }

    /// <summary>
    /// Basic stock information from master list
    /// </summary>
    public class StockInfo
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;
        public string? Isin { get; set; }
        public string? Sector { get; set; }
        public string? Industry { get; set; }
        public string? Series { get; set; }
        public bool IsFNOSec { get; set; }
        public string? Exchange { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? MarketCap { get; set; }
    }

    #endregion
    
}