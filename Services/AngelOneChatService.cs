using OtpNet;
using StockNotificationApi.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StockNotificationApi.Services
{
    public class AngelOneChatService : IAngelOneChatService
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ILogger<AngelOneService> _logger;

        private string _jwtToken;
        private DateTime _tokenExpiry;

        private const string BASE_URL = "https://apiconnect.angelbroking.com/";

        public AngelOneChatService(
            IConfiguration config,
            IHttpClientFactory factory,
            ILogger<AngelOneService> logger)
        {
            _config = config;
            _logger = logger;

            _httpClient = factory.CreateClient();
            _httpClient.BaseAddress = new Uri(BASE_URL);
        }

        private void SetHeaders()
        {
            _httpClient.DefaultRequestHeaders.Clear();

            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("X-PrivateKey", _config["AngelOne:ApiKey"]);
            _httpClient.DefaultRequestHeaders.Add("X-SourceID", "WEB");
            _httpClient.DefaultRequestHeaders.Add("X-UserType", "USER");

            _httpClient.DefaultRequestHeaders.Add("X-ClientLocalIP", "127.0.0.1");
            _httpClient.DefaultRequestHeaders.Add("X-ClientPublicIP", "127.0.0.1");
            _httpClient.DefaultRequestHeaders.Add("X-MACAddress", "00:00:00:00:00:00");

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _jwtToken);
        }

        public async Task AuthenticateAsync()
        {
            if (!string.IsNullOrEmpty(_jwtToken) && _tokenExpiry > DateTime.UtcNow)
                return;

            var totp = GenerateTotp(_config["AngelOne:TotpSecret"]);

            var body = new
            {
                clientcode = _config["AngelOne:ClientId"],
                password = _config["AngelOne:Mpin"],
                totp = totp
            };

            var json = JsonSerializer.Serialize(body);

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://apiconnect.angelbroking.com/rest/auth/angelbroking/user/v1/loginByPassword");

            request.Headers.Add("X-PrivateKey", _config["AngelOne:ApiKey"]);
            request.Headers.Add("X-SourceID", "WEB");
            request.Headers.Add("X-ClientLocalIP", "127.0.0.1");
            request.Headers.Add("X-ClientPublicIP", "127.0.0.1");
            request.Headers.Add("X-MACAddress", "00:00:00:00:00:00");
            request.Headers.Add("X-UserType", "USER");
            request.Headers.Add("Accept", "application/json");

            request.Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"AngelOne Auth Failed: {content}");

            var result = JsonSerializer.Deserialize<AngelAuthResponse>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _jwtToken = result.data.jwtToken;
            _tokenExpiry = DateTime.UtcNow.AddHours(2);

            _logger.LogInformation("AngelOne authenticated successfully");
        }

        public async Task<List<Holding>> GetHoldingsAsync()
        {
            await AuthenticateAsync();

            SetHeaders();

            var response = await _httpClient.GetAsync(
                "rest/portfolio/v1/getHolding");

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(content);
                return new List<Holding>();
            }

            var result = JsonSerializer.Deserialize<AngelHoldingResponse>(
    content,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.data == null)
            {
                _logger.LogError($"AngelOne returned empty holdings: {content}");
                return new List<Holding>();
            }

            return result.data.Select(x => new Holding
            {
                Symbol = x.tradingsymbol,
                Quantity = x.quantity + x.t1quantity,
                AveragePrice = x.averageprice,
                CurrentPrice = x.ltp
            }).ToList();
        }

        public async Task<List<Position>> GetPositionsAsync()
        {
            await AuthenticateAsync();

            SetHeaders();

            var response = await _httpClient.PostAsync(
                "rest/portfolio/v1/positions",
                null);

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(content);
                return new List<Position>();
            }

            //var result = JsonSerializer.Deserialize<PositionResponse>(
            //    content,
            //    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return null;
        }

        public async Task<string> PlaceOrderAsync(OrderRequest order)
        {
            await AuthenticateAsync();

            SetHeaders();

            var json = JsonSerializer.Serialize(order);

            var response = await _httpClient.PostAsync(
                "rest/order/v1/placeOrder",
                new StringContent(json, Encoding.UTF8, "application/json"));

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(content);

            var result = JsonSerializer.Deserialize<OrderResponse>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return result.OrderId;
        }

        private string GenerateTotp(string secret)
        {
            var key = Base32Encoding.ToBytes(secret);
            var totp = new Totp(key);

            return totp.ComputeTotp();
        }
    }
}
