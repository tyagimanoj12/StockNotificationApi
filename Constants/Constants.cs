// File: Constants.cs
namespace StockNotificationApi.Constants
{
    public static class CacheConstants
    {
        // Cache durations (in minutes)
        public const int STOCK_DATA_CACHE_MINUTES = 5;
        public const int ALL_STOCKS_CACHE_MINUTES = 15;
        public const int STOCK_LIST_CACHE_HOURS = 6;
        public const int HOLDINGS_CACHE_MINUTES = 5;
        public const int PORTFOLIO_CACHE_MINUTES = 5;
        public const int ANALYSIS_CACHE_MINUTES = 30;
        public const int BRIEFING_CACHE_MINUTES = 30;
        public const int TRADES_CACHE_MINUTES = 15;
        public const int TOP_TRADES_CACHE_MINUTES = 10;
        public const int CATEGORY_TRADES_CACHE_MINUTES = 15;
        public const int MARKET_NEWS_CACHE_MINUTES = 30;
        public const int STOCK_NEWS_CACHE_MINUTES = 60;
        public const int NEWS_SUMMARY_CACHE_MINUTES = 15;
    }

    public static class RateLimitConstants
    {
        // Rate limiting (in milliseconds)
        public const int RATE_LIMIT_DELAY_MS = 100;
        public const int API_RATE_LIMIT_DELAY_MS = 200;
        public const int COMMAND_COOLDOWN_SECONDS = 2;
        public const int MAX_COMMANDS_PER_MINUTE = 20;
        public const int RATE_LIMIT_WINDOW_MINUTES = 1;
        public const int SMS_SEND_DELAY_MS = 100;
        public const int NEWS_BATCH_SIZE = 5;
    }

    public static class TimeoutConstants
    {
        // Timeouts (in seconds/milliseconds)
        public const int HTTP_TIMEOUT_SECONDS = 30;
        public const int PRICE_FETCH_TIMEOUT_SECONDS = 5;
        public const int STOCK_FETCH_TIMEOUT_SECONDS = 10;
        public const int HEALTH_CHECK_INTERVAL_MS = 60000;
        public const int SCHEDULER_CHECK_INTERVAL_MS = 30000;
        public const int REPORT_SEND_DELAY_MS = 60000;
        public const int MAX_RETRY_ATTEMPTS = 3;
        public const int RETRY_DELAY_MS = 1000;
        public const int SMTP_TIMEOUT_MS = 30000;
    }

    public static class MarketConstants
    {
        // Market thresholds
        public const int LARGE_CAP_THRESHOLD = 20000;
        public const int MID_CAP_THRESHOLD = 5000;
        public const int BRIEFING_HOUR = 8;
        public const int BRIEFING_MINUTE = 30;
        public const int REPORT_HOUR = 9;
        public const int REPORT_MINUTE = 0;

        // Market hours
        public static readonly TimeSpan MARKET_OPEN_TIME = new(9, 15, 0);
        public static readonly TimeSpan MARKET_CLOSE_TIME = new(15, 30, 0);

        public static readonly DateTime MARKET_OPEN_TIME_DATETIME = new(9, 15, 0);
        public static readonly DateTime MARKET_CLOSE_TIME_DATETIME = new(15, 30, 0);
    }

    public static class TradingConstants
    {
        // Trading validation
        public const decimal MAX_REALISTIC_RETURN_PERCENT = 50m;
        public const decimal MIN_STOP_LOSS_DISTANCE = 5m;
        public const decimal MAX_STOP_LOSS_DISTANCE = 15m;
        public const decimal MAX_POSITION_SIZE_PERCENT = 0.05m;
        public const decimal MAX_DAILY_LOSS_PERCENT = 0.02m;
        public const decimal MAX_PORTFOLIO_RISK = 0.10m;
        public const int MAX_TRADES_PER_DAY = 5;
        public const int MIN_CONFIDENCE = 65;
    }

    public static class StockConstants
    {
        // Stock data
        public const int PRICE_COMMAND_PREFIX_LENGTH = 6;
        public const int ALERT_COMMAND_PREFIX_LENGTH = 6;
        public const int DEFAULT_STOCKS_PER_CATEGORY = 40;
        public const int MAX_STOCKS_PER_CATEGORY = 30;
        public const int DEFAULT_CONFIDENCE = 70;
        public const int LARGE_CAP_PICKS_REQUIRED = 5;
        public const int MID_CAP_PICKS_REQUIRED = 3;
        public const int SMALL_CAP_PICKS_REQUIRED = 3;
        public const int MAX_PICKS_PER_CATEGORY = 15;
        public const int DEFAULT_STOCK_COUNT = 200;
        public const int PROCESSING_BATCH_SIZE = 20;
        public const int FNO_LIST_SIZE = 100;

        // Multipliers
        public const decimal YEAR_HIGH_MULTIPLIER = 1.2m;
        public const decimal YEAR_LOW_MULTIPLIER = 0.8m;
        public const decimal DAY_HIGH_MULTIPLIER = 1.02m;
        public const decimal DAY_LOW_MULTIPLIER = 0.98m;
        public const decimal MARKET_CAP_DIVISOR = 10000000;

        // Thresholds
        public const decimal STRONG_MOMENTUM_THRESHOLD = 2m;
        public const decimal HIGH_CONFIDENCE_THRESHOLD = 5m;
        public const decimal MEDIUM_CONFIDENCE_THRESHOLD = 3m;
        public const decimal LOW_CONFIDENCE_THRESHOLD = 1m;
        public const decimal BUY_THRESHOLD = 2m;
        public const decimal SELL_THRESHOLD = -2m;
        public const decimal HIGH_RISK_THRESHOLD = 3m;
        public const decimal MEDIUM_RISK_THRESHOLD = 1m;
        public const decimal SIGNIFICANT_DECLINE_THRESHOLD = -2m;
        public const decimal BULLISH_THRESHOLD = 1m;
        public const decimal BEARISH_THRESHOLD = -1m;
    }

    public static class NewsConstants
    {
        // News
        public const int MAX_NEWS_ITEMS = 10;
        public const int TOP_NEWS_COUNT = 10;
        public const int MAX_NEWS_AGE_DAYS = 1;
        public const int YAHOO_NEWS_COUNT = 5;

        // Yahoo Finance symbols
        public const string YAHOO_NIFTY_SYMBOL = "^NSEI";
        public const string YAHOO_SENSEX_SYMBOL = "^BSESN";
        public const string YAHOO_BANKNIFTY_SYMBOL = "^NSEBANK";
    }

    public static class PortfolioConstants
    {
        // Portfolio
        public const int PORTFOLIO_DISPLAY_LIMIT = 5;
        public const int MAX_HOLDINGS_PER_USER = 50;
        public const int MIN_QUANTITY = 1;
        public const decimal MIN_PRICE = 0.01m;
        public const decimal PROFIT_THRESHOLD = 0m;
        public const int HEALTH_SCORE_BASE = 70;
    }

    public static class HealthCheckConstants
    {
        // Health checks
        public const int HEALTH_CHECK_INTERVAL_MS = 60000;
        public const int MAX_CONSECUTIVE_ERRORS = 5;
        public const int MEMORY_LIMIT_MB = 500;
        public const int MIN_FREE_DISK_MB = 1024;
    }

    public static class EndpointConstants
    {
        // Angel One endpoints
        public const string ANGEL_ONE_BASE_URL = "https://apiconnect.angelone.in/";
        public const string ANGEL_ONE_AUTH_ENDPOINT = "rest/auth/angelbroking/user/v1/loginByPassword";
        public const string ANGEL_ONE_HOLDINGS_ENDPOINT = "rest/secure/angelbroking/portfolio/v1/getHolding";
        public const string ANGEL_ONE_ORDER_ENDPOINT = "rest/order/v1/placeOrder";
        public const string ANGEL_ONE_POSITIONS_ENDPOINT = "rest/secure/angelbroking/portfolio/v1/getAllPositions";
        public const string ANGEL_ONE_POSITIONS_ENDPOINT_2 = "rest/portfolio/v1/positions";
        public const string ANGEL_ONE_POSITIONS_ENDPOINT_3 = "rest/secure/portfolio/v1/positions";
        public const string ANGEL_ONE_CANCEL_ORDER_ENDPOINT = "rest/order/v1/cancelOrder";
        public const string ANGEL_ONE_QUOTE_ENDPOINT = "rest/secure/angelbroking/market/v1/quote";



        // Groww endpoints
        public const string GROWW_BASE_URL = "https://api.groww.in/";
        public const string GROWW_AUTH_ENDPOINT = "v1/trading/auth/token";
        public const string GROWW_ORDER_ENDPOINT = "v1/trading/order";
        public const string GROWW_QUOTE_ENDPOINT = "v1/market/quote";
        public const string GROWW_POSITIONS_ENDPOINT = "v1/trading/positions";

        // NSE endpoints
        public const string NSE_MASTER_QUOTE_URL = "https://www.nseindia.com/api/master-quote";
        public const string NSE_QUOTE_URL = "https://www.nseindia.com/api/quote-equity?symbol={0}";

        // Yahoo Finance
        public const string YAHOO_QUOTE_URL = "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d";
        public const string YAHOO_SEARCH_URL = "https://query1.finance.yahoo.com/v1/finance/search?q={0}&newsCount={1}";
        public const string YAHOO_CHART_URL = "https://query1.finance.yahoo.com/v8/finance/chart/{0}?range={1}d&interval=1d";

        // BSE
        public const string BSE_QUOTE_URL = "https://api.bseindia.com/BseIndiaAPI/api/StockReachData/w?scripcode={0}";
    }

    public static class GeminiConstants
    {
        // Gemini API
        public const double DEFAULT_TEMPERATURE = 0.2;
        public const int DEFAULT_TOP_K = 1;
        public const int DEFAULT_TOP_P = 1;
        public const int DEFAULT_MAX_TOKENS = 2048;
        public const string DEFAULT_API_URL = "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent";
    }

    public static class SmsConstants
    {
        // SMS
        public const int MAX_MESSAGE_LENGTH = 160;
    }

    public static class CooldownConstants
    {
        // API cooldowns (in minutes)
        public const int ALPHA_VANTAGE_COOLDOWN = 24 * 60; // 24 hours
        public const int FREE_API_COOLDOWN = 1; // 1 minute
        public const int NSE_API_COOLDOWN = 0; // No cooldown
        public const int YAHOO_COOLDOWN = 1; // 1 minute
    }

    public static class TimeConstants
    {
        public const int TOKEN_EXPIRY_BUFFER_MINUTES = 5; // 5 minute
    }
}