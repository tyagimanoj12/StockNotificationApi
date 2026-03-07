using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public interface INotificationService
    {
        Task SendDailyPredictionReport(DailyPredictionReport report);
        Task SendTestNotification(string message);
    }
}