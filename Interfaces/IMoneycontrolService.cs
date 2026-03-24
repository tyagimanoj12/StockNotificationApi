using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IMoneycontrolService
    {
        Task<StockData?> GetQuoteAsync(string symbol);
        Task<List<StockNews>> GetStockNewsAsync(string symbol, int count = 10);
        Task<List<StockNews>> GetMarketNewsAsync(int count = 20);
        Task<Dictionary<string, decimal>> GetIndicesAsync();
        Task<List<StockData>> GetTopGainersAsync(string category = "all");
        Task<List<StockData>> GetTopLosersAsync(string category = "all");
    }
}
