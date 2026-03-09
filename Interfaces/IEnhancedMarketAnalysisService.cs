using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IEnhancedMarketAnalysisService
    {
        Task<EnhancedMarketAnalysis> AnalyzeMarketAsync();
        Task<string> GenerateDetailedReportAsync();
        Task<List<TopStockPick>> GetTopPicksByCategoryAsync(string category, int count = 5);
        Task<Dictionary<string, object>> GetMarketTechnicalIndicatorsAsync();
    }

}
