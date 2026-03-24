using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Services
{
    public class MarketStatusService : IMarketStatusService
    {
        // Market hours from Constants.cs
        private readonly TimeSpan _marketOpenTime = MarketConstants.MARKET_OPEN_TIME;
        private readonly TimeSpan _marketCloseTime = MarketConstants.MARKET_CLOSE_TIME;

        // Market holidays for 2024 (add more as needed)
        private readonly HashSet<DateTime> _marketHolidays = new HashSet<DateTime>
        {
            new DateTime(DateTime.Now.Year, 1, 26), // Republic Day
            new DateTime(DateTime.Now.Year, 3, 8),  // Mahashivratri
            new DateTime(DateTime.Now.Year, 3, 25), // Holi
            new DateTime(DateTime.Now.Year, 3, 29), // Good Friday
            new DateTime(DateTime.Now.Year, 4, 11), // Id-ul-Fitr
            new DateTime(DateTime.Now.Year, 4, 17), // Ram Navami
            new DateTime(DateTime.Now.Year, 5, 1),  // Maharashtra Day
            new DateTime(DateTime.Now.Year, 6, 17), // Bakri Id
            new DateTime(DateTime.Now.Year, 7, 17), // Muharram
            new DateTime(DateTime.Now.Year, 8, 15), // Independence Day
            new DateTime(DateTime.Now.Year, 9, 16), // Ganesh Chaturthi
            new DateTime(DateTime.Now.Year, 10, 2), // Gandhi Jayanti
            new DateTime(DateTime.Now.Year, 10, 31), // Diwali
            new DateTime(DateTime.Now.Year, 11, 15), // Gurunanak Jayanti
            new DateTime(DateTime.Now.Year, 12, 25) // Christmas
        };

        public bool IsMarketOpen()
        {
            var now = DateTime.Now;

            // Check if it's a holiday
            if (IsHoliday(now))
                return false;

            // Check weekend
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            // Check market hours
            var currentTime = now.TimeOfDay;
            return currentTime >= _marketOpenTime && currentTime <= _marketCloseTime;
        }

        public DateTime GetNextMarketOpenTime()
        {
            var now = DateTime.Now;
            var nextDay = now;

            // If market is currently open, return today's open time (already passed)
            if (IsMarketOpen())
            {
                return new DateTime(now.Year, now.Month, now.Day,
                    _marketOpenTime.Hours, _marketOpenTime.Minutes, 0);
            }

            // Find next market open day
            for (int i = 1; i <= 7; i++) // Check next 7 days
            {
                nextDay = now.AddDays(i);
                nextDay = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                    _marketOpenTime.Hours, _marketOpenTime.Minutes, 0);

                if (IsTradingDay(nextDay))
                {
                    return nextDay;
                }
            }

            // Fallback (should never reach here)
            return now.AddDays(1).Date.Add(_marketOpenTime);
        }

        public DateTime GetNextMarketCloseTime()
        {
            var now = DateTime.Now;

            // If market is open, return today's close time
            if (IsMarketOpen())
            {
                return new DateTime(now.Year, now.Month, now.Day,
                    _marketCloseTime.Hours, _marketCloseTime.Minutes, 0);
            }

            // If market is closed, return next trading day's close time
            var nextOpenDay = GetNextMarketOpenTime();
            return new DateTime(nextOpenDay.Year, nextOpenDay.Month, nextOpenDay.Day,
                _marketCloseTime.Hours, _marketCloseTime.Minutes, 0);
        }

        public string GetMarketStatusMessage()
        {
            var isOpen = IsMarketOpen();
            var now = DateTime.Now;

            if (isOpen)
            {
                var closeTime = GetNextMarketCloseTime();
                var timeLeft = closeTime - now;

                return $"🟢 <b>Market is OPEN</b>\n\n" +
                       $"Current Time: {now:HH:mm:ss}\n" +
                       $"Close Time: {closeTime:HH:mm}\n" +
                       $"Time Left: {timeLeft.Hours}h {timeLeft.Minutes}m\n" +
                       $"Trading Session: Regular";
            }
            else
            {
                // Check if it's a holiday
                if (IsHoliday(now))
                {
                    var nextOpen = GetNextMarketOpenTime();
                    return $"🔴 <b>Market is CLOSED (Holiday)</b>\n\n" +
                           $"Date: {now:dd MMM yyyy}\n" +
                           $"Next Open: {nextOpen:dd MMM yyyy, HH:mm}";
                }

                // Check if it's weekend
                if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                {
                    var nextOpen = GetNextMarketOpenTime();
                    return $"🔴 <b>Market is CLOSED (Weekend)</b>\n\n" +
                           $"Next Open: {nextOpen:dd MMM yyyy, HH:mm}";
                }

                // After market hours
                var nextOpenTime = GetNextMarketOpenTime();
                var timeUntil = nextOpenTime - now;

                return $"🔴 <b>Market is CLOSED</b>\n\n" +
                       $"Current Time: {now:HH:mm:ss}\n" +
                       $"Next Open: {nextOpenTime:HH:mm}\n" +
                       $"Time Until Open: {timeUntil.Hours}h {timeUntil.Minutes}m";
            }
        }

        public bool IsTradingDay(DateTime date)
        {
            // Check if it's a holiday
            if (IsHoliday(date))
                return false;

            // Check if it's weekend
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                return false;

            return true;
        }

        private bool IsHoliday(DateTime date)
        {
            // Check if the date is in the holidays list (ignore year)
            return _marketHolidays.Any(h => h.Month == date.Month && h.Day == date.Day);
        }

        public TimeSpan GetTimeUntilNextEvent()
        {
            var now = DateTime.Now;

            if (IsMarketOpen())
            {
                return GetNextMarketCloseTime() - now;
            }
            else
            {
                return GetNextMarketOpenTime() - now;
            }
        }

        public string GetTradingSession()
        {
            if (!IsMarketOpen())
                return "Closed";

            var now = DateTime.Now;
            var currentTime = now.TimeOfDay;

            // Pre-open session: 9:00 AM to 9:15 AM
            if (currentTime >= new TimeSpan(9, 0, 0) && currentTime < _marketOpenTime)
                return "Pre-open Session";

            // Regular trading: 9:15 AM to 3:30 PM
            if (currentTime >= _marketOpenTime && currentTime <= _marketCloseTime)
                return "Regular Trading";

            // Post-close: After 3:30 PM
            return "Post-close Session";
        }
    }
}