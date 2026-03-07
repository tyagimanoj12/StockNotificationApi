using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface INotificationService
    {
        Task SendDailyPredictionReport(DailyPredictionReport report);
        Task SendTestNotification(string message);
    }
}