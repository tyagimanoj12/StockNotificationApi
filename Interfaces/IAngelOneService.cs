using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IAngelOneService
    {
        Task<AngelOneSession> AuthenticateAsync();
        Task<OrderResponse> PlaceOrderAsync(OrderRequest order);
        Task<List<Holding>> GetHoldingsAsync();
        Task<PortfolioSummary> GetPortfolioAsync();
        Task<bool> CancelOrderAsync(string orderId);
        Task<ExecutionResult> ExecuteTradeAsync(TradeAction action);
        Task<List<Position>> GetPositionsAsync();
    }
}