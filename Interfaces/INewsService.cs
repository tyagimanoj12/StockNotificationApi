using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface INewsService
    {
        Task<List<StockNews>> GetStockNewsAsync(string symbol, int days = 1);
        Task<List<StockNews>> GetTopMarketNewsAsync(int count = 10);
        Task<Dictionary<string, List<StockNews>>> GetNewsForSymbolsAsync(List<string> symbols);
        Task<string> GetNewsSummaryAsync(List<StockNews> news);
    }
}
