using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface ITelegramBotService
    {
        Task SendMessageAsync(long chatId, string message, Telegram.Bot.Types.Enums.ParseMode parseMode = Telegram.Bot.Types.Enums.ParseMode.Html);
        Task BroadcastToAllAsync(string message);
        Task SendDailyReportToAllAsync(DailyPredictionReport report);
    }
}
