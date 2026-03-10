using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockNotificationApi.Services
{
    /// <summary>
    /// Service for interacting with Groww trading API
    /// </summary>
    public class GrowwService : IGrowwService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<GrowwService> _logger;
        private readonly HttpClient _httpClient;
        private GrowwSession _session;
        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        private string _accessToken;

        // Constants
        private const string BASE_URL = "https://api.groww.in/";
        private const string AUTH_ENDPOINT = "v1/trading/auth/token";
        private const string ORDER_ENDPOINT = "v1/trading/order";
        private const string QUOTE_ENDPOINT = "v1/market/quote";
        private const string POSITIONS_ENDPOINT = "v1/trading/positions"; // Hypothetical endpoint

        /// <summary>
        /// Initializes a new instance of the <see cref="GrowwService"/> class.
        /// </summary>
        /// <param name="configuration">Configuration settings</param>
        /// <param name="httpClientFactory">HTTP client factory</param>
        /// <param name="logger">Logger instance</param>
        /// <exception cref="ArgumentNullException">Thrown when any dependency is null</exception>
        public GrowwService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<GrowwService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClientFactory.CreateClient("Groww");

            _httpClient.BaseAddress = new Uri(BASE_URL);
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        #region Authentication

        /// <summary>
        /// Authenticates with Groww API and returns a session token
        /// </summary>
        /// <returns>GrowwSession containing access token and expiry</returns>
        /// <exception cref="InvalidOperationException">Thrown when credentials are not configured</exception>
        public async Task<GrowwSession> AuthenticateAsync()
        {
            await _authLock.WaitAsync();
            try
            {
                _logger.LogInformation("Authenticating with Groww...");

                var apiKey = _configuration["Groww:ApiKey"];
                var apiSecret = _configuration["Groww:ApiSecret"];

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    _logger.LogError("Groww API credentials not configured");
                    throw new InvalidOperationException("Groww API credentials not configured");
                }

                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var checksum = GenerateChecksum(apiSecret, timestamp);

                var request = new
                {
                    key_type = "approval",
                    checksum = checksum,
                    timestamp = timestamp
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(request),
                    Encoding.UTF8,
                    "application/json"
                );

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", apiKey);
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

                var response = await _httpClient.PostAsync(AUTH_ENDPOINT, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<GrowwAuthResponse>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (result != null)
                    {
                        _session = new GrowwSession
                        {
                            AccessToken = result.token,
                            Expiry = result.expiry
                        };

                        _accessToken = result.token;
                        _logger.LogInformation("Successfully authenticated with Groww. Token expires at {Expiry}", result.expiry);

                        return _session;
                    }
                }

                _logger.LogError("Authentication failed: {StatusCode} - {Error}", response.StatusCode, responseContent);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error authenticating with Groww");
                throw;
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// <summary>
        /// Generates HMAC SHA256 checksum for authentication
        /// </summary>
        private string GenerateChecksum(string secret, string timestamp)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Ensures the user is authenticated before making API calls
        /// </summary>
        private async Task EnsureAuthenticatedAsync()
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                await AuthenticateAsync();
                return;
            }

            // Check if token is expired (if we have expiry info)
            if (_session != null && _session.Expiry < DateTime.UtcNow.AddMinutes(5))
            {
                _logger.LogInformation("Token expires soon, re-authenticating...");
                await AuthenticateAsync();
            }
        }

        /// <summary>
        /// Sets authentication headers for API requests
        /// </summary>
        private void SetAuthHeaders()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("X-API-VERSION", "1.0");
        }

        #endregion

        #region Order Operations

        /// <summary>
        /// Places an order on Groww
        /// </summary>
        /// <param name="order">Order details including symbol, quantity, price</param>
        /// <returns>Order response with status and order ID</returns>
        /// <exception cref="ArgumentNullException">Thrown when order is null</exception>
        public async Task<OrderResponse> PlaceOrderAsync(OrderRequest order)
        {
            if (order == null) throw new ArgumentNullException(nameof(order));

            await EnsureAuthenticatedAsync();

            try
            {
                _logger.LogInformation("Placing order on Groww: {Action} {Quantity} {Symbol} @ ₹{Price:F2}",
                    order.Action, order.Quantity, order.Symbol, order.Price);

                var request = new
                {
                    symbol = order.Symbol,
                    exchange = order.Exchange ?? "NSE",
                    transaction_type = order.Action.ToUpperInvariant(),
                    quantity = order.Quantity,
                    price = order.Price,
                    order_type = order.OrderType ?? "LIMIT",
                    product = order.ProductType ?? "DELIVERY",
                    validity = order.Duration ?? "DAY"
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(request),
                    Encoding.UTF8,
                    "application/json"
                );

                SetAuthHeaders();

                var response = await _httpClient.PostAsync(ORDER_ENDPOINT, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<GrowwOrderResponse>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (result?.data != null)
                    {
                        _logger.LogInformation("Order placed successfully on Groww: {OrderId}", result.data.order_id);

                        return new OrderResponse
                        {
                            OrderId = result.data.order_id,
                            Status = "SUCCESS",
                            Message = "Order placed successfully",
                            OrderTime = DateTime.Now
                        };
                    }
                }

                _logger.LogError("Order failed on Groww: {StatusCode} - {Error}", response.StatusCode, responseContent);

                return new OrderResponse
                {
                    Status = "FAILED",
                    Message = $"Order failed: {responseContent}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing order on Groww for {Symbol}", order.Symbol);
                throw;
            }
        }

        #endregion

        #region Market Data

        /// <summary>
        /// Gets live price for a symbol from Groww
        /// </summary>
        /// <param name="symbol">Stock symbol (e.g., "RELIANCE")</param>
        /// <returns>Current market price</returns>
        /// <exception cref="ArgumentException">Thrown when symbol is empty</exception>
        public async Task<decimal> GetLivePriceAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

            try
            {
                _logger.LogDebug("Fetching live price for {Symbol}", symbol);

                var response = await _httpClient.GetAsync($"{QUOTE_ENDPOINT}/{symbol}.NSE");
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var quote = JsonSerializer.Deserialize<GrowwQuote>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (quote != null)
                    {
                        _logger.LogDebug("Price for {Symbol}: ₹{Price:F2}", symbol, quote.ltp);
                        return quote.ltp;
                    }
                }

                _logger.LogWarning("Failed to fetch price for {Symbol}: {StatusCode}", symbol, response.StatusCode);
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching live price for {Symbol}", symbol);
                throw;
            }
        }

        #endregion

        #region Portfolio Operations

        /// <summary>
        /// Gets current positions/holdings from Groww
        /// </summary>
        /// <returns>List of positions with quantities and prices</returns>
        public async Task<List<Position>> GetPositionsAsync()
        {
            await EnsureAuthenticatedAsync();

            try
            {
                _logger.LogInformation("Fetching positions from Groww...");

                // Note: This endpoint may need to be updated based on Groww's actual API
                var response = await _httpClient.GetAsync(POSITIONS_ENDPOINT);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<GrowwPositionsResponse>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (result?.data != null)
                    {
                        var positions = new List<Position>();

                        foreach (var p in result.data)
                        {
                            // Create Position object - PnL and PnLPercent are calculated automatically
                            var position = new Position
                            {
                                Symbol = p.symbol ?? string.Empty,
                                Quantity = p.quantity,
                                BuyPrice = p.average_price,
                                CurrentPrice = p.ltp
                                // Don't set PnL or PnLPercent - they're calculated
                            };

                            positions.Add(position);

                            _logger.LogDebug("Position: {Symbol} {Quantity} @ ₹{BuyPrice:F2} now ₹{CurrentPrice:F2}",
                                position.Symbol, position.Quantity, position.BuyPrice, position.CurrentPrice);
                        }

                        _logger.LogInformation("Fetched {Count} positions from Groww", positions.Count);
                        return positions;
                    }
                    else
                    {
                        _logger.LogWarning("No position data found in response");
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Positions endpoint not found. This may not be supported by Groww API.");
                    // Return empty list instead of throwing
                    return new List<Position>();
                }
                else
                {
                    _logger.LogWarning("Failed to fetch positions: {StatusCode} - {Error}",
                        response.StatusCode, responseContent);
                }

                return new List<Position>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching positions from Groww");
                // Return empty list instead of throwing to prevent service disruption
                return new List<Position>();
            }
        }
        #endregion
    }
}