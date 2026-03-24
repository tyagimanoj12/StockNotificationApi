using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IMessageFormatter
    {
        string FormatDailyReport(DailyPredictionReport report);
    }
}
