namespace StockNotificationApi.Models
{
    #region Utility Models

    /// <summary>
    /// API rate limit tracking information
    /// </summary>
    public class RateLimitInfo
    {
        public string ApiName { get; set; } = string.Empty;
        public DateTime CooldownUntil { get; set; }
        public DateTime LastFailureTime { get; set; }
        public int FailureCount { get; set; }
    }

    #endregion
}
