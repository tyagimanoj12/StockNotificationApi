using StockNotificationApi.Models;
using StockNotificationApi.Services;

namespace StockNotificationApi.Interfaces
{
    public interface IGrowwService
    {
        Task<GrowwSession> AuthenticateAsync();
        Task<OrderResponse> PlaceOrderAsync(OrderRequest order);
        Task<decimal> GetLivePriceAsync(string symbol);
        Task<List<Position>> GetPositionsAsync();
    }
}
