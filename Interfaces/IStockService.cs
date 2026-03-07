using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IStockService
    {
        Task<List<StockData>> GetIndianStockDataAsync();
        Task<StockData> GetStockDataAsync(string symbol);
    }
}