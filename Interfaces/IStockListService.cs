using StockNotificationApi.Models;
namespace StockNotificationApi.Interfaces
{
    public interface IStockListService
    {
        Task<List<StockInfo>> GetNSEStocksAsync();
        Task<List<StockInfo>> GetBSEStocksAsync();
        Task<List<StockInfo>> GetTopStocksByMarketCapAsync(int count = 50);
        Task<List<StockInfo>> GetStocksBySectorAsync(string sector);
        Task<List<string>> GetFNOSymbolsAsync(int count = 50);
        Task RefreshStockListAsync();
        Task<List<StockInfo>> GetLargeCapStocksAsync(int count = 20);
        Task<List<StockInfo>> GetMidCapStocksAsync(int count = 20);
        Task<List<StockInfo>> GetSmallCapStocksAsync(int count = 20);
    }
}
