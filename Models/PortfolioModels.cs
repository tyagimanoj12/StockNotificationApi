using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    #region Portfolio Models

    /// <summary>
    /// User portfolio data
    /// </summary>
    public class UserPortfolio
    {
        [Required]
        [Range(1, long.MaxValue)]
        public long ChatId { get; set; }

        public string Username { get; set; } = string.Empty;
        public List<PortfolioHolding> Holdings { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Individual stock holding in portfolio
    /// </summary>
    public class PortfolioHolding
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;

        [Range(1, int.MaxValue)]
        public int Quantity { get; set; }

        [Range(0.01, double.MaxValue)]
        public decimal BuyPrice { get; set; }

        public DateTime BuyDate { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Portfolio performance metrics
    /// </summary>
    public class PortfolioPerformance
    {
        public decimal TotalInvestment { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal TotalPL { get; set; }
        public decimal TotalPLPercent { get; set; }
        public decimal TodayPL { get; set; }
        public List<HoldingPerformance> Holdings { get; set; } = new();
        public Dictionary<string, decimal> SectorAllocation { get; set; } = new();
    }

    /// <summary>
    /// Individual holding performance
    /// </summary>
    public class HoldingPerformance
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal CurrentValue => Quantity * CurrentPrice;
        public decimal Investment => Quantity * BuyPrice;
        public decimal UnrealizedPL => CurrentValue - Investment;
        public decimal UnrealizedPLPercent => Investment > 0 ? (UnrealizedPL / Investment) * 100 : 0;
        public decimal DayChange { get; set; }
        public decimal DayChangePercent { get; set; }
        public string Sector { get; set; } = string.Empty;
    }

    /// <summary>
    /// Portfolio summary with current values
    /// </summary>
    public class PortfolioSummary
    {
        public decimal TotalInvestment { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal TotalProfitLoss => CurrentValue - TotalInvestment;
        public decimal TotalProfitLossPercent => TotalInvestment > 0 ? (TotalProfitLoss / TotalInvestment) * 100 : 0;
        public List<Holding> Holdings { get; set; } = new();
        public Dictionary<string, decimal> SectorAllocation { get; set; } = new();
        public DateTime AsOfDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Basic holding information
    /// </summary>
    public class Holding
    {
        public string Symbol { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal AveragePrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal ProfitLoss { get; set; }
        public decimal ProfitLossPercent => AveragePrice > 0 ? ((CurrentPrice - AveragePrice) / AveragePrice) * 100 : 0;
    }

    #endregion
}
