using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface INseApiService
    {
        Task<NSEQuoteResponse?> GetQuoteAsync(string symbol);
        Task<List<string>> GetSymbolsAsync();
        Task<bool> TestConnectionAsync();
        Task<NSEQuoteResponse?> GetQuoteWithRetryAsync(string symbol, int maxRetries = 3);
    }
}
