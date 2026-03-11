// Interfaces/IMarketStatusService.cs
namespace StockNotificationApi.Interfaces
{
    public interface IMarketStatusService
    {
        /// <summary>
        /// Checks if the market is currently open
        /// </summary>
        bool IsMarketOpen();

        /// <summary>
        /// Gets the next time the market will open
        /// </summary>
        DateTime GetNextMarketOpenTime();

        /// <summary>
        /// Gets the next time the market will close
        /// </summary>
        DateTime GetNextMarketCloseTime();

        /// <summary>
        /// Gets a formatted message about market status
        /// </summary>
        string GetMarketStatusMessage();

        /// <summary>
        /// Checks if a given date is a trading day
        /// </summary>
        bool IsTradingDay(DateTime date);

        /// <summary>
        /// Gets the time until the next market event (open or close)
        /// </summary>
        TimeSpan GetTimeUntilNextEvent();

        /// <summary>
        /// Gets the current trading session (Pre-open, Regular, Post-close)
        /// </summary>
        string GetTradingSession();
    }
}