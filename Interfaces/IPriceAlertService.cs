// 1. Update IPriceAlertService interface with new methods
using StockNotificationApi.Services;

public interface IPriceAlertService
{
    Task AddAlertAsync(long chatId, string symbol, decimal targetPrice, bool isAbove);
    Task<List<PriceAlertService.UserAlert>> GetUserAlertsAsync(long chatId);
    Task<bool> RemoveAlertAsync(long chatId, int alertId);
    Task ClearTriggeredAlertsAsync(long chatId);
    Task<int> GetActiveAlertCountAsync(long chatId); // Add this
    Task<Dictionary<long, List<PriceAlertService.UserAlert>>> GetAllAlertsAsync(); // Add this
    Task CleanupOldAlertsAsync(int daysOld = 7); // Add this
}