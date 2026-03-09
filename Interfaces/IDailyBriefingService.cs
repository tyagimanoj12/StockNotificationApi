// Interfaces/IDailyBriefingService.cs
using StockNotificationApi.Models;

namespace StockNotificationApi.Interfaces
{
    public interface IDailyBriefingService
    {
        Task<DailyBriefing> GenerateDailyBriefingAsync();
        Task SendBriefingToUserAsync(long chatId);
        Task SendBriefingToAllSubscribersAsync();
        string FormatBriefingForTelegram(DailyBriefing briefing);
    }
}