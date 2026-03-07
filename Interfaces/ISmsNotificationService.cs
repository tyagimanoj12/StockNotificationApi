using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface ISmsNotificationService
    {
        Task SendSmsAsync(string to, string message);
        Task SendPredictionAlertAsync(StockPrediction prediction);
    }
}
