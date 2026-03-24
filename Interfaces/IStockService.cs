// Interfaces/IStockService.cs
using StockNotificationApi.Models;
using static StockNotificationApi.Services.MemoryCacheService;

namespace StockNotificationApi.Interfaces
{
    public interface IStockService
    {
        Task<List<StockData>> GetIndianStockDataAsync();
        Task<StockData?> GetStockDataAsync(string symbol);
        Task<List<StockData>> GetMultipleQuotesAsync(List<string> symbols); // Add this method
        void ClearCache();
        Task<CacheStatistics> GetCacheStatisticsAsync();
    }
}