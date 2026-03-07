using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IAIService
    {
        Task<DailyPredictionReport> GeneratePredictionsAsync(List<StockData> stockData);
        Task<string> GetMarketInsightAsync(List<StockData> stockData);
    }
}