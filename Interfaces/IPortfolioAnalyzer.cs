using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IPortfolioAnalyzer
    {
        int CalculateEnhancedHealthScore(List<Holding> holdings, decimal totalValue, decimal totalInvestment, Dictionary<string, decimal> sectorExposure);
        List<string> GenerateEnhancedWarnings(List<Holding> holdings, PortfolioSummary portfolio, Dictionary<string, decimal> sectorExposure);
        string CalculateMomentum(List<Holding> holdings);
        string CalculateDiversificationScore(Dictionary<string, decimal> sectorExposure, int holdingCount);
        string EstimateRecoveryTime(decimal lossPercent);
        string GenerateHeatMap(decimal percentage);
    }
}
