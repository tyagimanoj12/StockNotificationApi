using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    #region Telegram Models

    /// <summary>
    /// Telegram user subscription data
    /// </summary>
    public class TelegramUser
    {
        [Required]
        [Range(1, long.MaxValue)]
        public long ChatId { get; set; }

        public string Username { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }

        public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;

        public List<string> PreferredStocks { get; set; } = new();
        public NotificationPreferences Preferences { get; set; } = new();
    }

    /// <summary>
    /// User notification preferences
    /// </summary>
    public class NotificationPreferences
    {
        public bool DailyReport { get; set; } = true;
        public bool PriceAlerts { get; set; } = true;
        public bool MarketOpenAlert { get; set; } = false;
        public bool TopGainersLosers { get; set; } = true;
    }

    #endregion
}
