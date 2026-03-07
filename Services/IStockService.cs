using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public interface IStockService
    {
        Task<List<StockData>> GetIndianStockDataAsync();
        Task<StockData> GetStockDataAsync(string symbol);
    }
}