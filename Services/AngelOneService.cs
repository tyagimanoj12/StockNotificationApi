using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Data;
using System.Text;
using System.Text.Json;
using OtpNet;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockNotificationApi.Services
{
    public class AngelOneService : IAngelOneService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AngelOneService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpClient _httpClient;
        private AngelOneSession _session;
        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        private string _accessToken;
        private string _userId;
        private string _feedToken;

        // Store these during authentication for consistency across requests
        private string _clientLocalIP;
        private string _clientPublicIP;
        private string _macAddress;

        // Constants for API endpoints
        private const string BASE_URL = "https://apiconnect.angelone.in/";
        private const string AUTH_ENDPOINT = "rest/auth/angelbroking/user/v1/loginByPassword";

        // Holdings endpoint - exactly as per Java example
        private const string HOLDINGS_ENDPOINT = "rest/secure/angelbroking/portfolio/v1/getHolding";

        // Other endpoints
        private const string ORDER_ENDPOINT = "rest/order/v1/placeOrder";
        private const string POSITIONS_ENDPOINT = "rest/secure/angelbroking/portfolio/v1/getAllPositions";
        private const string POSITIONS_ENDPOINT_2 = "rest/portfolio/v1/positions";
        private const string POSITIONS_ENDPOINT_3 = "rest/secure/portfolio/v1/positions";
        private const string CANCEL_ORDER_ENDPOINT = "rest/order/v1/cancelOrder";

        public AngelOneService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<AngelOneService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _httpClient = httpClientFactory.CreateClient("AngelOne");

            _httpClient.BaseAddress = new Uri(BASE_URL);
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        #region Authentication

        public async Task<AngelOneSession> AuthenticateAsync()
        {
            await _authLock.WaitAsync();
            try
            {
                _logger.LogInformation("Authenticating with Angel One...");

                var apiKey = _configuration["AngelOne:ApiKey"];
                var clientId = _configuration["AngelOne:ClientId"];
                var mpin = _configuration["AngelOne:Mpin"];
                var totpSecret = _configuration["AngelOne:TotpSecret"];

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(clientId) ||
                    string.IsNullOrEmpty(mpin) || string.IsNullOrEmpty(totpSecret))
                {
                    _logger.LogError("Angel One credentials not configured");
                    throw new InvalidOperationException("Angel One credentials not configured");
                }

                var totp = GenerateTOTP(totpSecret);
                _logger.LogDebug("Generated TOTP: {Totp}", totp);

                var request = new
                {
                    clientcode = clientId,
                    password = mpin,
                    totp = totp,
                    api_key = apiKey
                };

                var json = JsonSerializer.Serialize(request);
                _logger.LogDebug("Auth Request: {Json}", json);

                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Get actual IP and MAC addresses for this session
                _clientLocalIP = await GetLocalIPAddressAsync();
                _clientPublicIP = await GetPublicIPAsync();
                _macAddress = GetMacAddress();

                _logger.LogInformation("Using IPs - Local: {LocalIP}, Public: {PublicIP}, MAC: {MAC}",
                    _clientLocalIP, _clientPublicIP, _macAddress);

                SetAuthHeaders(apiKey);

                var response = await _httpClient.PostAsync(AUTH_ENDPOINT, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogDebug("Auth Response Status: {StatusCode}", response.StatusCode);
                _logger.LogDebug("Auth Response: {Content}", responseContent);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<AngelOneAuthResponse>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (result?.data != null)
                    {
                        _session = new AngelOneSession
                        {
                            AuthToken = result.data.jwtToken,
                            RefreshToken = result.data.refreshToken,
                            FeedToken = result.data.feedToken,
                            UserId = result.data.userId,
                            ExpiresAt = DateTime.UtcNow.AddHours(2)
                        };

                        _accessToken = result.data.jwtToken;
                        _userId = result.data.userId;
                        _feedToken = result.data.feedToken;

                        _logger.LogInformation("✅ Successfully authenticated with Angel One");
                        _logger.LogInformation("User ID: {UserId}, Token expires at: {Expiry}", _userId, _session.ExpiresAt);

                        return _session;
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogError("❌ IP Address not whitelisted. Add this IP to Angel One portal: {Ip}", _clientPublicIP);
                }
                else
                {
                    _logger.LogError("❌ Authentication failed: {StatusCode} - {Error}",
                        response.StatusCode, responseContent);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error authenticating with Angel One");
                throw;
            }
            finally
            {
                _authLock.Release();
            }
        }

        private void SetAuthHeaders(string apiKey)
        {
            _httpClient.DefaultRequestHeaders.Clear();

            _httpClient.DefaultRequestHeaders.Add("X-UserType", "USER");
            _httpClient.DefaultRequestHeaders.Add("X-SourceID", "WEB");
            _httpClient.DefaultRequestHeaders.Add("X-ClientLocalIP", _clientLocalIP);
            _httpClient.DefaultRequestHeaders.Add("X-ClientPublicIP", _clientPublicIP);
            _httpClient.DefaultRequestHeaders.Add("X-MACAddress", _macAddress);
            _httpClient.DefaultRequestHeaders.Add("X-PrivateKey", apiKey);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        private void SetSessionHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.Clear();

            // Add headers in the EXACT order as Java example
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
            client.DefaultRequestHeaders.Add("Content-Type", "application/json");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("X-UserType", "USER");
            client.DefaultRequestHeaders.Add("X-SourceID", "WEB");
            client.DefaultRequestHeaders.Add("X-ClientLocalIP", _clientLocalIP ?? "127.0.0.1");
            client.DefaultRequestHeaders.Add("X-ClientPublicIP", _clientPublicIP ?? "127.0.0.1");
            client.DefaultRequestHeaders.Add("X-MACAddress", _macAddress ?? "00-00-00-00-00-00");
            client.DefaultRequestHeaders.Add("X-PrivateKey", _configuration["AngelOne:ApiKey"]);

            // Add feed token if available
            if (!string.IsNullOrEmpty(_feedToken))
            {
                client.DefaultRequestHeaders.Add("X-FeedToken", _feedToken);
            }

            _logger.LogDebug("Session Headers set: {Headers}", string.Join(", ",
                client.DefaultRequestHeaders.Select(h => $"{h.Key}: {string.Join(",", h.Value)}")));
        }

        private async Task EnsureAuthenticatedAsync()
        {
            if (_session == null || string.IsNullOrEmpty(_accessToken))
            {
                _logger.LogInformation("No active session, authenticating...");
                await AuthenticateAsync();
                return;
            }

            if (_session.ExpiresAt < DateTime.UtcNow.AddMinutes(5))
            {
                _logger.LogInformation("Token expires soon, re-authenticating...");
                await AuthenticateAsync();
            }
        }

        private string GenerateTOTP(string secret)
        {
            var key = Base32Encoding.ToBytes(secret);
            var totp = new Totp(key, step: 30, totpSize: 6);
            return totp.ComputeTotp();
        }

        private async Task<string> GetPublicIPAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                return await client.GetStringAsync("https://api.ipify.org");
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private async Task<string> GetLocalIPAddressAsync()
        {
            try
            {
                var hostName = System.Net.Dns.GetHostName();
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName);
                var ipv4 = addresses.FirstOrDefault(a =>
                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !System.Net.IPAddress.IsLoopback(a));

                return ipv4?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private string GetMacAddress()
        {
            try
            {
                var mac = System.Net.NetworkInformation.NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                           && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .Select(nic => nic.GetPhysicalAddress().ToString())
                    .FirstOrDefault();

                return !string.IsNullOrEmpty(mac) ? mac : "00-00-00-00-00-00";
            }
            catch
            {
                return "00-00-00-00-00-00";
            }
        }

        #endregion

        #region Portfolio Operations

        public async Task<List<Holding>> GetHoldingsAsync()
        {
            await EnsureAuthenticatedAsync();

            if (string.IsNullOrEmpty(_accessToken))
            {
                _logger.LogError("AngelOne not authenticated");
                return new List<Holding>();
            }

            try
            {
                _logger.LogInformation("Fetching holdings from Angel One...");

                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(BASE_URL);
                client.Timeout = TimeSpan.FromSeconds(30);

                // Clear any default headers
                client.DefaultRequestHeaders.Clear();

                // Add ONLY request headers to DefaultRequestHeaders
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("X-UserType", "USER");
                client.DefaultRequestHeaders.Add("X-SourceID", "WEB");
                client.DefaultRequestHeaders.Add("X-ClientLocalIP", _clientLocalIP ?? "127.0.0.1");
                client.DefaultRequestHeaders.Add("X-ClientPublicIP", _clientPublicIP ?? "127.0.0.1");
                client.DefaultRequestHeaders.Add("X-MACAddress", _macAddress ?? "00-00-00-00-00-00");
                client.DefaultRequestHeaders.Add("X-PrivateKey", _configuration["AngelOne:ApiKey"]);

                // CRITICAL: Create GET request with null body
                var request = new HttpRequestMessage(HttpMethod.Get, HOLDINGS_ENDPOINT)
                {
                    Content = null  // No content, exactly like Java's .method("GET", null)
                };                

                _logger.LogInformation("Making GET request to: {BaseUrl}{Endpoint}", BASE_URL, HOLDINGS_ENDPOINT);

                // Log headers for debugging
                _logger.LogDebug("Request Headers: {Headers}", string.Join(", ",
                    client.DefaultRequestHeaders.Select(h => $"{h.Key}: {string.Join(",", h.Value)}")));

                var response = await client.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("Holdings Response Status: {StatusCode}", response.StatusCode);
                _logger.LogInformation("Holdings Response Body: {Response}", responseContent);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<AngelOneHoldingsResponse>(
                        responseContent,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (result?.status == true && result.data != null)
                    {
                        var holdings = result.data.Select(h => new Holding
                        {
                            Symbol = h.tradingsymbol,
                            Quantity = h.quantity + (h.t1quantity),
                            AveragePrice = h.averageprice,
                            CurrentPrice = h.ltp,
                            ProfitLoss = h.profitloss ?? h.profitandloss
                        }).ToList();

                        _logger.LogInformation("✅ Successfully fetched {Count} holdings", holdings.Count);
                        return holdings;
                    }
                    else
                    {
                        _logger.LogWarning("Holdings response status false or no data: {Status}", result?.status);
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Token expired, re-authenticating...");
                    _session = null;
                    _accessToken = null;
                    return await GetHoldingsAsync();
                }
                else
                {
                    _logger.LogError("❌ Holdings request failed: {StatusCode}", response.StatusCode);
                    _logger.LogError("Response: {Response}", responseContent);
                }

                return new List<Holding>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching holdings");
                return new List<Holding>();
            }
        }

        public async Task<List<Position>> GetPositionsAsync()
        {
            await EnsureAuthenticatedAsync();

            try
            {
                _logger.LogInformation("Fetching positions from Angel One...");

                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(BASE_URL);
                client.Timeout = TimeSpan.FromSeconds(30);

                SetSessionHeaders(client);

                // Try different endpoints in order
                var endpoints = new[]
                {
                    POSITIONS_ENDPOINT,
                    POSITIONS_ENDPOINT_2,
                    POSITIONS_ENDPOINT_3
                };

                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        _logger.LogDebug("Trying endpoint: {Endpoint}", endpoint);

                        HttpResponseMessage response;

                        if (endpoint.Contains("getAllPositions"))
                        {
                            // Some endpoints use POST with empty body
                            var requestBody = new
                            {
                                clientcode = _userId ?? _configuration["AngelOne:ClientId"]
                            };
                            var content = new StringContent(
                                JsonSerializer.Serialize(requestBody),
                                Encoding.UTF8,
                                "application/json"
                            );
                            response = await client.PostAsync(endpoint, content);
                        }
                        else
                        {
                            response = await client.GetAsync(endpoint);
                        }

                        var responseContent = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            var result = JsonSerializer.Deserialize<AngelOnePositionsResponse>(
                                responseContent,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            if (result?.status == true && result.data != null)
                            {
                                var positions = result.data.Select(p => new Position
                                {
                                    Symbol = p.tradingsymbol,
                                    Quantity = p.quantity,
                                    BuyPrice = p.buyprice,
                                    CurrentPrice = p.ltp
                                }).ToList();

                                _logger.LogInformation("✅ Successfully fetched {Count} positions from {Endpoint}",
                                    positions.Count, endpoint);
                                return positions;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error with endpoint {Endpoint}, trying next...", endpoint);
                    }
                }

                _logger.LogError("All endpoints failed for fetching positions");
                return new List<Position>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching positions");
                return new List<Position>();
            }
        }

        public async Task<PortfolioSummary> GetPortfolioAsync()
        {
            var holdings = await GetHoldingsAsync();

            var summary = new PortfolioSummary
            {
                TotalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice),
                CurrentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice),
                Holdings = holdings,
                AsOfDate = DateTime.Now
            };

            _logger.LogInformation("Portfolio Summary - Value: ₹{CurrentValue:N2}, Investment: ₹{TotalInvestment:N2}, P&L: ₹{Pnl:N2} ({Percent:F2}%)",
                summary.CurrentValue, summary.TotalInvestment, summary.TotalProfitLoss,
                summary.TotalInvestment > 0 ? (summary.TotalProfitLoss / summary.TotalInvestment) * 100 : 0);

            return summary;
        }

        #endregion

        #region Order Operations

        public async Task<OrderResponse> PlaceOrderAsync(OrderRequest order)
        {
            if (order == null) throw new ArgumentNullException(nameof(order));

            await EnsureAuthenticatedAsync();

            try
            {
                _logger.LogInformation("Placing order: {Action} {Quantity} {Symbol} @ ₹{Price:F2}",
                    order.Action, order.Quantity, order.Symbol, order.Price);

                var request = new
                {
                    variety = order.Variety ?? "NORMAL",
                    tradingsymbol = order.Symbol,
                    symboltoken = GetSymbolToken(order.Symbol),
                    transactiontype = order.Action.ToUpperInvariant(),
                    exchange = order.Exchange ?? "NSE",
                    ordertype = order.OrderType ?? "LIMIT",
                    producttype = order.ProductType ?? "DELIVERY",
                    duration = order.Duration ?? "DAY",
                    price = order.Price.ToString("F2"),
                    squareoff = "0",
                    stoploss = "0",
                    quantity = order.Quantity.ToString()
                };

                var json = JsonSerializer.Serialize(request);
                _logger.LogDebug("Order Request: {Json}", json);

                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(BASE_URL);
                client.Timeout = TimeSpan.FromSeconds(30);

                SetSessionHeaders(client);

                var response = await client.PostAsync(ORDER_ENDPOINT, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogDebug("Order Response Status: {StatusCode}", response.StatusCode);
                _logger.LogDebug("Order Response: {Content}", responseContent);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<AngelOneOrderResponse>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (result?.data != null)
                    {
                        _logger.LogInformation("✅ Order placed successfully: {OrderId}", result.data.orderid);

                        return new OrderResponse
                        {
                            OrderId = result.data.orderid,
                            Status = "SUCCESS",
                            Message = "Order placed successfully",
                            OrderTime = DateTime.Now,
                            ExecutedPrice = order.Price,
                            ExecutedQuantity = order.Quantity
                        };
                    }
                }

                _logger.LogError("❌ Order failed: {StatusCode} - {Error}", response.StatusCode, responseContent);

                return new OrderResponse
                {
                    Status = "FAILED",
                    Message = $"Order failed: {responseContent}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing order for {Symbol}", order.Symbol);
                throw;
            }
        }

        public async Task<bool> CancelOrderAsync(string orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
                throw new ArgumentException("Order ID cannot be empty", nameof(orderId));

            await EnsureAuthenticatedAsync();

            try
            {
                _logger.LogInformation("Cancelling order: {OrderId}", orderId);

                var request = new
                {
                    variety = "NORMAL",
                    orderid = orderId
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(BASE_URL);
                client.Timeout = TimeSpan.FromSeconds(30);

                SetSessionHeaders(client);

                var response = await client.PostAsync(CANCEL_ORDER_ENDPOINT, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("✅ Order cancelled successfully: {OrderId}", orderId);
                    return true;
                }

                _logger.LogError("❌ Failed to cancel order {OrderId}: {Error}", orderId, responseContent);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling order {OrderId}", orderId);
                throw;
            }
        }

        public async Task<ExecutionResult> ExecuteTradeAsync(TradeAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                _logger.LogInformation("Executing trade: {Action} {Quantity} {Symbol} at ₹{Price:F2}",
                    action.Action, action.Quantity, action.Symbol, action.Price);

                var orderRequest = new OrderRequest
                {
                    Symbol = action.Symbol,
                    Action = action.Action,
                    Quantity = action.Quantity,
                    Price = action.Price,
                    Exchange = "NSE",
                    Variety = "NORMAL",
                    OrderType = "LIMIT",
                    ProductType = "DELIVERY",
                    Duration = "DAY"
                };

                var response = await PlaceOrderAsync(orderRequest);

                var result = new ExecutionResult
                {
                    Success = response.Status == "SUCCESS",
                    OrderId = response.OrderId,
                    Symbol = action.Symbol,
                    Quantity = action.Quantity,
                    ExecutedPrice = action.Price,
                    Message = response.Message,
                    ExecutionTime = DateTime.Now
                };

                if (result.Success)
                {
                    _logger.LogInformation("✅ Trade executed successfully: {OrderId} for {Symbol}",
                        result.OrderId, action.Symbol);
                }
                else
                {
                    _logger.LogWarning("❌ Trade execution failed: {Message}", response.Message);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing trade for {Symbol}", action.Symbol);
                return new ExecutionResult
                {
                    Success = false,
                    Symbol = action.Symbol,
                    Message = ex.Message,
                    ExecutionTime = DateTime.Now
                };
            }
        }

        #endregion

        #region Helper Methods

        private string GetSymbolToken(string symbol)
        {
            var token = SymbolTokenMap.GetAngelOneToken(symbol);

            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }

            _logger.LogWarning("Unknown symbol token for {Symbol}", symbol);
            throw new Exception($"Unknown symbol token for {symbol}. Please add to SymbolTokenMap.");
        }

        #endregion
    }
}