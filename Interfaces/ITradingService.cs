using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface ITradingService
    {
        /// <summary>
        /// Gets today's trading dashboard with all trade recommendations
        /// </summary>
        /// <param name="minConfidence">Minimum confidence threshold (default: 70)</param>
        /// <returns>Trading dashboard with market context and trade recommendations</returns>
        Task<TradingDashboard> GetTodaysTradesAsync(int minConfidence = 70);

        /// <summary>
        /// Gets the top N trades by confidence
        /// </summary>
        /// <param name="count">Number of top trades to return (default: 5)</param>
        /// <returns>List of top trade recommendations</returns>
        Task<List<TradeItem>> GetTopTradesAsync(int count = 5);

        /// <summary>
        /// Gets trades by specific category (Large/Mid/Small Cap)
        /// </summary>
        /// <param name="category">Category name: "large", "mid", or "small"</param>
        /// <param name="minConfidence">Minimum confidence threshold (default: 70)</param>
        /// <returns>List of trade recommendations for the specified category</returns>
        Task<List<TradeItem>> GetTradesByCategoryAsync(string category, int minConfidence = 70);
    }
}