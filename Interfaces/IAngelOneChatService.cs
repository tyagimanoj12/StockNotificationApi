using StockNotificationApi.Models;

public interface IAngelOneChatService
{
    Task AuthenticateAsync();

    Task<List<Holding>> GetHoldingsAsync();

    Task<List<Position>> GetPositionsAsync();

    Task<string> PlaceOrderAsync(OrderRequest request);
}