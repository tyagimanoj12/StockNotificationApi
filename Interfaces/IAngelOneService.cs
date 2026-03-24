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
        Task<StockData?> GetLiveQuoteAsync(string symbol);
        Task<List<StockData>> GetMultipleQuotesAsync(List<string> symbols);
        Task<MarketIndices?> GetIndicesAsync();
        //Task<List<AngelOneCandle>> GetHistoricalDataAsync(string symbol, string interval = "DAY", int days = 30);
        Task<List<AngelOneMasterQuote>> GetMasterQuoteAsync(string exchange = "NSE", CancellationToken cancellationToken = default);
        Task<Dictionary<string, List<AngelOneMasterQuote>>> GetAllMasterQuotesAsync();
    }
}