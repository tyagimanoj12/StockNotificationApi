using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IGoogleFinanceService
    {
        Task<StockData?> GetQuoteAsync(string symbol);
        Task<List<StockData>> GetMarketMoversAsync(string exchange = "NSE");
        Task<Dictionary<string, decimal>> GetCurrencyRatesAsync();
    }
}
