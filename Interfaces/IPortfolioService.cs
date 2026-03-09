using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IPortfolioService
    {
        Task<UserPortfolio?> GetUserPortfolioAsync(long chatId);
        Task<bool> AddHoldingAsync(long chatId, string username, PortfolioHolding holding);
        Task<bool> RemoveHoldingAsync(long chatId, string symbol);
        Task<bool> UpdateHoldingAsync(long chatId, PortfolioHolding holding);
        Task<PortfolioPerformance?> GetPortfolioPerformanceAsync(long chatId);
        Task<List<UserPortfolio>> GetAllPortfoliosAsync();
        Task SavePortfolioAsync(UserPortfolio portfolio);
    }
}