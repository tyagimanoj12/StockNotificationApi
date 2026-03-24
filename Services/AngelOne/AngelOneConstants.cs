using StockNotificationApi.Constants;

namespace StockNotificationApi.Services.AngelOne
{
    public static class AngelOneConstants
    {
        // API Endpoints
        public const string BASE_URL = EndpointConstants.ANGEL_ONE_BASE_URL;
        public const string AUTH_ENDPOINT = EndpointConstants.ANGEL_ONE_AUTH_ENDPOINT;
        public const string HOLDINGS_ENDPOINT = EndpointConstants.ANGEL_ONE_HOLDINGS_ENDPOINT;
        public const string ORDER_ENDPOINT = EndpointConstants.ANGEL_ONE_ORDER_ENDPOINT;
        public const string POSITIONS_ENDPOINT = EndpointConstants.ANGEL_ONE_POSITIONS_ENDPOINT;
        public const string POSITIONS_ENDPOINT_2 = EndpointConstants.ANGEL_ONE_POSITIONS_ENDPOINT_2;
        public const string POSITIONS_ENDPOINT_3 = EndpointConstants.ANGEL_ONE_POSITIONS_ENDPOINT_3;
        public const string CANCEL_ORDER_ENDPOINT = EndpointConstants.ANGEL_ONE_CANCEL_ORDER_ENDPOINT;
        public const string QUOTE_ENDPOINT = EndpointConstants.ANGEL_ONE_QUOTE_ENDPOINT;

        // Cache constants
        public const int HOLDINGS_CACHE_MINUTES = CacheConstants.HOLDINGS_CACHE_MINUTES;
        public const int PORTFOLIO_CACHE_MINUTES = CacheConstants.PORTFOLIO_CACHE_MINUTES;
        public const int QUOTE_CACHE_MINUTES = 5;
        public const int INDICES_CACHE_MINUTES = 2;

        // Authentication
        public const int AUTH_COOLDOWN_SECONDS = 10;

        // ETF/MF Detection Patterns
        public static readonly string[] ValidStocksWithNumbers = new[]
        {
            "63MOONS", "20MICRONS", "3MINDIA", "5PAISA",
            "9", "10", "13", "HDFCBANK", "ICICIBANK",
            "SBIN", "KOTAKBANK", "AXISBANK"
        };

        public static readonly string[] ClearETFPatterns = new[]
        {
            "INAV", "BEES", "MUTUAL", "ETF", "JUNIORBEES",
            "NIFTYBEES", "BANKBEES", "ITBEES", "LIQUIDBEES",
            "MON100", "HDFC50INAV", "DSPN50INAV", "EBBE32INAV",
            "MONQ50INAV", "MAM150INAV", "HSM250INAV"
        };

        public static readonly string[] InvalidSuffixes = new[]
        {
            "-SG", "-GB", "-MF", "-SM", "-ST", "-IV",
            "-ND", "-NG", "-NH", "-Z5", "-Z6", "-Z7",
            "-N0", "-N1", "-N5", "-N8"
        };
    }
}