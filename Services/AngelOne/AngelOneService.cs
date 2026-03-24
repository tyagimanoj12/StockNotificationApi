//using StockNotificationApi.Interfaces;
//using StockNotificationApi.Services.AngelOne.Models;
//using System.Text;
//using System.Text.Json;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public partial class AngelOneService : IAngelOneService
//    {
//        private readonly IConfiguration _configuration;
//        private readonly ILogger<AngelOneService> _logger;
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly ICacheService _cache;
//        private readonly HttpClient _httpClient;

//        // Static authentication state
//        private static readonly SemaphoreSlim _globalAuthLock = new(1, 1);
//        private static string _globalAccessToken = string.Empty;
//        private static string _globalUserId = string.Empty;
//        private static string _globalFeedToken = string.Empty;
//        private static DateTime _globalTokenExpiry = DateTime.MinValue;
//        private static DateTime _lastAuthAttempt = DateTime.MinValue;
//        private static bool _isAuthenticating = false;

//        // Network identifiers
//        private string _clientLocalIP = string.Empty;
//        private string _clientPublicIP = string.Empty;
//        private string _macAddress = string.Empty;

//        public AngelOneService(
//            IConfiguration configuration,
//            IHttpClientFactory httpClientFactory,
//            ILogger<AngelOneService> logger,
//            ICacheService cache)
//        {
//            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
//            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
//            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
//            _httpClient = httpClientFactory.CreateClient("AngelOne");
//            _httpClient.BaseAddress = new Uri(AngelOneConstants.BASE_URL);
//            _httpClient.Timeout = TimeSpan.FromSeconds(30);
//            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
//        }
//    }
//}