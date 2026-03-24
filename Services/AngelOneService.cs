using OtpNet;
using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text.Json;

namespace StockNotificationApi.Services.AngelOne
{
    public partial class AngelOneService : IAngelOneService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AngelOneService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ICacheService _cache;
        private readonly HttpClient _httpClient;

        // Static authentication state - shared across ALL instances
        private static readonly SemaphoreSlim _globalAuthLock = new(1, 1);
        private static string _globalAccessToken = string.Empty;
        private static string _globalUserId = string.Empty;
        private static string _globalFeedToken = string.Empty;
        private static DateTime _globalTokenExpiry = DateTime.MinValue;
        private static DateTime _lastAuthAttempt = DateTime.MinValue;
        private static bool _isAuthenticating = false;

        private const int AUTH_COOLDOWN_SECONDS = 10;

        // Network identifiers (instance-specific)
        private string _clientLocalIP = string.Empty;
        private string _clientPublicIP = string.Empty;
        private string _macAddress = string.Empty;
        
        private const int QUOTE_CACHE_MINUTES = 5;
        private const int INDICES_CACHE_MINUTES = 2;

        public AngelOneService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<AngelOneService> logger,
            ICacheService cache)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _httpClient = httpClientFactory.CreateClient("AngelOne");
            _httpClient.BaseAddress = new Uri(AngelOneConstants.BASE_URL);
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
        }

        #region Authentication

        /// <summary>
        /// PUBLIC interface implementation - called from outside
        /// </summary>
        public async Task<AngelOneSession> AuthenticateAsync()
        {
            return await AuthenticateInternalAsync();
        }

        /// <summary>
        /// Internal authentication logic - SINGLE POINT OF AUTHENTICATION
        /// </summary>
        private async Task<AngelOneSession> AuthenticateInternalAsync()
        {
            // Fast path - token is already valid (no lock needed for reads)
            if (await IsTokenValidAsync())
            {
                _logger.LogDebug("Token already valid, returning existing session");
                return new AngelOneSession
                {
                    AuthToken = _globalAccessToken,
                    UserId = _globalUserId,
                    FeedToken = _globalFeedToken,
                    ExpiresAt = _globalTokenExpiry
                };
            }

            // Check auth cooldown
            if (!await CanAttemptAuthAsync())
            {
                _logger.LogWarning("Authentication cooldown active");
                return null;
            }

            await _globalAuthLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (await IsTokenValidAsync())
                {
                    _logger.LogDebug("Token became valid while waiting for lock");
                    return new AngelOneSession
                    {
                        AuthToken = _globalAccessToken,
                        UserId = _globalUserId,
                        FeedToken = _globalFeedToken,
                        ExpiresAt = _globalTokenExpiry
                    };
                }

                // Prevent multiple simultaneous auth attempts
                if (_isAuthenticating)
                {
                    _logger.LogWarning("Authentication already in progress, waiting...");
                    var maxWait = TimeSpan.FromSeconds(30);
                    var startTime = DateTime.UtcNow;

                    while (_isAuthenticating && DateTime.UtcNow - startTime < maxWait)
                    {
                        await Task.Delay(100);
                        if (await IsTokenValidAsync())
                        {
                            return new AngelOneSession
                            {
                                AuthToken = _globalAccessToken,
                                UserId = _globalUserId,
                                FeedToken = _globalFeedToken,
                                ExpiresAt = _globalTokenExpiry
                            };
                        }
                    }
                }

                _isAuthenticating = true;
                await UpdateLastAuthAttemptAsync();

                _logger.LogInformation("Authenticating with Angel One...");

                var credentials = GetCredentials();
                var totp = GenerateTOTP(credentials.TotpSecret);

                _logger.LogDebug("Generated TOTP: {Totp}", totp);

                var request = new
                {
                    clientcode = credentials.ClientId,
                    password = credentials.Mpin,
                    totp,
                    api_key = credentials.ApiKey
                };

                await InitializeNetworkIdentifiersAsync();
                await ConfigureClientHeadersAsync(_httpClient, false, false);
                _httpClient.DefaultRequestHeaders.Add("X-PrivateKey", credentials.ApiKey);

                var response = await _httpClient.PostAsync(AngelOneConstants.AUTH_ENDPOINT, CreateJsonContent(request));
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogDebug("Auth Response Status: {StatusCode}", response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    return await ProcessAuthResponseAsync(responseContent);
                }

                await HandleAuthErrorAsync(response, responseContent);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error authenticating with Angel One");
                throw;
            }
            finally
            {
                _isAuthenticating = false;
                _globalAuthLock.Release();
            }
        }

        private async Task<bool> IsTokenValidAsync()
        {
            await Task.CompletedTask; // For async pattern
            lock (typeof(AngelOneService))
            {
                return !string.IsNullOrEmpty(_globalAccessToken) &&
                       _globalTokenExpiry > DateTime.UtcNow.AddMinutes(TimeConstants.TOKEN_EXPIRY_BUFFER_MINUTES);
            }
        }

        private async Task<bool> CanAttemptAuthAsync()
        {
            await Task.CompletedTask;
            lock (typeof(AngelOneService))
            {
                if ((DateTime.UtcNow - _lastAuthAttempt).TotalSeconds < AUTH_COOLDOWN_SECONDS)
                {
                    return false;
                }
                return true;
            }
        }

        private async Task UpdateLastAuthAttemptAsync()
        {
            await Task.CompletedTask;
            lock (typeof(AngelOneService))
            {
                _lastAuthAttempt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// SINGLE METHOD to ensure authentication - called from all API methods
        /// </summary>
        private async Task EnsureAuthenticatedAsync()
        {
            // Fast path - token is valid
            if (await IsTokenValidAsync())
            {
                return;
            }

            // Slow path - authenticate
            await AuthenticateInternalAsync();
        }

        private async Task<string> GetAccessTokenAsync()
        {
            await EnsureAuthenticatedAsync();
            lock (typeof(AngelOneService))
            {
                return _globalAccessToken;
            }
        }

        private async Task<string> GetUserIdAsync()
        {
            await EnsureAuthenticatedAsync();
            lock (typeof(AngelOneService))
            {
                return _globalUserId;
            }
        }

        private async Task<string> GetFeedTokenAsync()
        {
            await EnsureAuthenticatedAsync();
            lock (typeof(AngelOneService))
            {
                return _globalFeedToken;
            }
        }

        private AngelOneCredentials GetCredentials()
        {
            var apiKey = _configuration["AngelOne:ApiKey"];
            var clientId = _configuration["AngelOne:ClientId"];
            var mpin = _configuration["AngelOne:Mpin"];
            var totpSecret = _configuration["AngelOne:TotpSecret"];

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(clientId) ||
                string.IsNullOrEmpty(mpin) || string.IsNullOrEmpty(totpSecret))
            {
                throw new InvalidOperationException("Angel One credentials not configured");
            }

            return new AngelOneCredentials(apiKey, clientId, mpin, totpSecret);
        }

        private async Task InitializeNetworkIdentifiersAsync()
        {
            _clientLocalIP = await GetLocalIPAddressAsync();
            _clientPublicIP = await GetPublicIPAsync();
            _macAddress = GetMacAddress();

            _logger.LogInformation("Using IPs - Local: {LocalIP}, Public: {PublicIP}, MAC: {MAC}",
                _clientLocalIP, _clientPublicIP, _macAddress);
        }

        private async Task<AngelOneSession> ProcessAuthResponseAsync(string responseContent)
        {
            var result = JsonSerializer.Deserialize<AngelOneAuthResponse>(responseContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.data == null)
            {
                _logger.LogError("Invalid auth response: no data");
                return null;
            }

            var session = new AngelOneSession
            {
                AuthToken = result.data.jwtToken,
                RefreshToken = result.data.refreshToken,
                FeedToken = result.data.feedToken,
                UserId = result.data.userId,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            };

            // Update static globals
            lock (typeof(AngelOneService))
            {
                _globalAccessToken = result.data.jwtToken;
                _globalUserId = result.data.userId;
                _globalFeedToken = result.data.feedToken;
                _globalTokenExpiry = session.ExpiresAt;
            }

            _logger.LogInformation("✅ Successfully authenticated with Angel One");
            _logger.LogInformation("User ID: {UserId}, Token expires at: {Expiry}", _globalUserId, session.ExpiresAt);

            return session;
        }

        private async Task HandleAuthErrorAsync(HttpResponseMessage response, string responseContent)
        {
            await Task.CompletedTask;
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogError("❌ IP Address not whitelisted. Add this IP to Angel One portal: {Ip}", _clientPublicIP);
            }
            else
            {
                _logger.LogError("❌ Authentication failed: {StatusCode} - {Error}", response.StatusCode, responseContent);
            }
        }

        private async Task ConfigureClientHeadersAsync(HttpClient client, bool includeAuth = true, bool isMarketData = false)
        {
            await Task.CompletedTask;
            client.DefaultRequestHeaders.Clear();

            if (includeAuth)
            {
                var token = _globalAccessToken;
                if (!string.IsNullOrEmpty(token))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                }
            }

            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("X-UserType", "USER");
            client.DefaultRequestHeaders.Add("X-SourceID", "WEB");
            client.DefaultRequestHeaders.Add("X-ClientLocalIP", _clientLocalIP ?? "127.0.0.1");
            client.DefaultRequestHeaders.Add("X-ClientPublicIP", _clientPublicIP ?? "127.0.0.1");
            client.DefaultRequestHeaders.Add("X-MACAddress", _macAddress ?? "00-00-00-00-00-00");

            var apiKey = _configuration["AngelOne:ApiKey"];
            client.DefaultRequestHeaders.Add("X-PrivateKey", apiKey);

            var feedToken = _globalFeedToken;
            if (!string.IsNullOrEmpty(feedToken) && !isMarketData)
            {
                client.DefaultRequestHeaders.Add("X-FeedToken", feedToken);
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
            return await ExecuteAuthenticatedRequestAsync(
                $"angelone_holdings_{await GetUserIdAsync()}",
                AngelOneConstants.HOLDINGS_CACHE_MINUTES,
                async (client) =>
                {
                    await ConfigureClientHeadersAsync(client, true, false);
                    var request = new HttpRequestMessage(HttpMethod.Get, AngelOneConstants.HOLDINGS_ENDPOINT) { Content = null };
                    var response = await client.SendAsync(request);
                    return await HandleHoldingsResponseAsync(response);
                });
        }

        public async Task<List<Position>> GetPositionsAsync()
        {
            await EnsureAuthenticatedAsync();
            return await ExecuteAuthenticatedRequestAsync(
                $"angelone_positions_{await GetUserIdAsync()}",
                AngelOneConstants.HOLDINGS_CACHE_MINUTES,
                async (client) =>
                {
                    await ConfigureClientHeadersAsync(client, true, false);
                    var endpoints = new[] { AngelOneConstants.POSITIONS_ENDPOINT, AngelOneConstants.POSITIONS_ENDPOINT_2, AngelOneConstants.POSITIONS_ENDPOINT_3 };

                    foreach (var endpoint in endpoints)
                    {
                        var positions = await TryGetPositionsFromEndpointAsync(client, endpoint);
                        if (positions != null) return positions;
                    }

                    return new List<Position>();
                });
        }

        private async Task<List<Position>?> TryGetPositionsFromEndpointAsync(HttpClient client, string endpoint)
        {
            try
            {
                HttpResponseMessage response;

                if (endpoint.Contains("getAllPositions"))
                {
                    var requestBody = new { clientcode = await GetUserIdAsync() ?? _configuration["AngelOne:ClientId"] };
                    response = await client.PostAsync(endpoint, CreateJsonContent(requestBody));
                }
                else
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, endpoint) { Content = null };
                    response = await client.SendAsync(request);
                }

                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<AngelOnePositionsResponse>(responseContent,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (result?.status == true && result.data != null)
                    {
                        return result.data.Select(p => new Position
                        {
                            Symbol = p.tradingsymbol,
                            Quantity = p.quantity,
                            BuyPrice = p.buyprice,
                            CurrentPrice = p.ltp
                        }).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error with endpoint {Endpoint}", endpoint);
            }

            return null;
        }

        public async Task<PortfolioSummary> GetPortfolioAsync()
        {
            return await ExecuteAuthenticatedRequestAsync(
                $"angelone_portfolio_{await GetUserIdAsync()}",
                AngelOneConstants.PORTFOLIO_CACHE_MINUTES,
                async (_) =>
                {
                    var holdings = await GetHoldingsAsync();

                    var summary = new PortfolioSummary
                    {
                        TotalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice),
                        CurrentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice),
                        Holdings = holdings,
                        AsOfDate = DateTime.Now
                    };

                    var sectorAllocation = new Dictionary<string, decimal>();
                    foreach (var holding in holdings)
                    {
                        var sector = GetSectorFromSymbol(holding.Symbol);
                        sectorAllocation[sector] = sectorAllocation.GetValueOrDefault(sector) +
                                                  holding.Quantity * holding.CurrentPrice;
                    }

                    if (summary.CurrentValue > 0)
                    {
                        summary.SectorAllocation = sectorAllocation.ToDictionary(
                            kv => kv.Key,
                            kv => kv.Value / summary.CurrentValue * 100);
                    }

                    return summary;
                });
        }

        #endregion

        #region Market Data Operations

        public async Task<StockData?> GetLiveQuoteAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;

            await EnsureAuthenticatedAsync();

            return await ExecuteWithRetryAsync(async () =>
            {
                var cleanSymbol = CleanSymbol(symbol);

                // Try to get token - if fails, return null gracefully
                string? symbolToken = null;
                try
                {
                    symbolToken = GetSymbolToken(cleanSymbol);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping {Symbol} - {Message}", symbol, ex.Message);
                    return null;
                }

                if (string.IsNullOrEmpty(symbolToken)) return null;

                return await ExecuteMarketDataRequestAsync(
                    $"angelone_quote_{cleanSymbol}",
                    QUOTE_CACHE_MINUTES,
                    async (client) =>
                    {
                        await ConfigureClientHeadersAsync(client, true, true);
                        var requestBody = new
                        {
                            mode = "FULL",
                            exchangeTokens = new Dictionary<string, List<string>>
                            {
                                { "NSE", new List<string> { symbolToken } }
                            }
                        };

                        var quotes = await FetchMarketDataAsync(client, requestBody);
                        var quote = quotes?.FirstOrDefault();
                        return quote != null ? MapMarketQuoteToStockData(quote, "NSE") : null;
                    });
            }, $"GetLiveQuote_{symbol}");
        }

        /// <summary>
        /// Enhanced ETF/Mutual Fund detection - filters out all non-tradable symbols
        /// </summary>
        private bool IsETFOrMutualFund(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return true;

            var upperSymbol = symbol.ToUpper().Trim();

            // ========== FIRST, CHECK IF IT'S A VALID STOCK ==========
            // Known valid stocks that might have numbers in name
            var validStocksWithNumbers = new[]
            {
        "63MOONS", "20MICRONS", "3MINDIA", "5PAISA",
        "9", "10", "13",  // Nifty, Sensex, BankNifty tokens
        "HDFCBANK", "ICICIBANK", "SBIN", "KOTAKBANK"
    };

            if (validStocksWithNumbers.Any(v => upperSymbol.Contains(v)))
            {
                return false; // NOT an ETF/MF, it's a valid stock
            }

            // ========== NOW CHECK FOR ETF/MF PATTERNS ==========
            // Only filter if it's clearly an ETF/MF
            var clearETFPatterns = new[]
            {
        "INAV",        // ETF indicator
        "BEES",        // ETF suffix
        "MUTUAL",      // Mutual fund
        "ETF",         // ETF keyword
        "JUNIORBEES",  // Known ETF
        "NIFTYBEES",   // Known ETF
        "BANKBEES",    // Known ETF
        "ITBEES",      // Known ETF
        "LIQUIDBEES",  // Known ETF
        "MON100",      // Known ETF
        "HDFC50INAV",  // ETF
        "DSPN50INAV",  // ETF
        "EBBE32INAV",  // ETF
    };

            if (clearETFPatterns.Any(p => upperSymbol.Contains(p)))
            {
                _logger.LogDebug("Filtered ETF: {Symbol} contains {Pattern}", symbol, clearETFPatterns.First(p => upperSymbol.Contains(p)));
                return true;
            }

            // Check suffixes - only filter common non-tradable suffixes
            var invalidSuffixes = new[] { "-SG", "-GB", "-MF", "-SM", "-ST", "-IV", "-ND", "-NG", "-NH" };
            if (invalidSuffixes.Any(s => upperSymbol.EndsWith(s)))
            {
                _logger.LogDebug("Filtered by suffix: {Symbol}", symbol);
                return true;
            }

            // DON'T filter symbols with numbers unless they match ETF patterns
            // Let the token lookup handle invalid symbols

            return false;
        }

        private bool IsValidStockSymbol(string symbol)
        {
            // Known valid stock symbols that might contain numbers
            var validStockPatterns = new[]
            {
                "63MOONS", "20MICRONS", "3MINDIA", "5PAISA", "63", "MOONS",
                "3M", "5", "6", "7", "8", "9", "20", "MICRONS", "INDIGO",
                "LUPIN", "BIOCON", "APOLLO", "LT", "M&M", "HDFC", "ICICI",
                "SBIN", "RELIANCE", "TCS", "INFY", "WIPRO", "HCLTECH", "TECHM",
                "TATAMOTORS", "TATASTEEL", "TATACONSUM", "ADANIPORTS", "ADANIENT",
                "ADANIGREEN", "ADANITRANS", "ZOMATO", "DMART", "VEDL", "IRCTC",
                "LTI", "MINDTREE", "PERSISTENT", "LTTS", "KPITTECH", "MAPMYINDIA",
                "KAYNES", "ANGELONE", "PNBHOUSING"
            };

            return validStockPatterns.Any(p => symbol.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<List<StockData>> GetMultipleQuotesAsync(List<string> symbols)
        {
            if (symbols == null || !symbols.Any()) return new List<StockData>();

            // ========== AGGRESSIVE ETF/MF FILTERING ==========
            var filteredSymbols = new List<string>();
            var skippedSymbols = new List<string>();

            foreach (var symbol in symbols)
            {
                var cleanSymbol = CleanSymbol(symbol);
                if (IsETFOrMutualFund(cleanSymbol))
                {
                    skippedSymbols.Add(symbol);
                    _logger.LogDebug("Filtered out ETF/MF symbol: {Symbol}", symbol);
                }
                else
                {
                    filteredSymbols.Add(symbol);
                }
            }

            if (skippedSymbols.Any())
            {
                _logger.LogInformation("Filtered out {Count} ETF/MF symbols: {Symbols}",
                    skippedSymbols.Count, string.Join(", ", skippedSymbols.Take(10)));
            }

            if (!filteredSymbols.Any())
            {
                _logger.LogInformation("All symbols filtered out as ETF/MF, returning empty list");
                return new List<StockData>();
            }

            _logger.LogInformation("Fetching quotes for {Count} valid symbols (filtered from {Total})",
                filteredSymbols.Count, symbols.Count);

            await EnsureAuthenticatedAsync();

            var results = new List<StockData>();
            var validTokens = new Dictionary<string, List<string>>();
            var symbolToTokenMap = new Dictionary<string, string>();

            foreach (var symbol in filteredSymbols)
            {
                var cleanSymbol = CleanSymbol(symbol);
                string? token = null;

                try
                {
                    token = GetSymbolToken(cleanSymbol);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping {Symbol} - Token not found", symbol);
                    continue;
                }

                if (string.IsNullOrEmpty(token)) continue;

                var exchange = DetermineExchange(symbol);
                if (!validTokens.ContainsKey(exchange))
                    validTokens[exchange] = new List<string>();

                validTokens[exchange].Add(token);
                symbolToTokenMap[$"{exchange}_{token}"] = cleanSymbol;
            }

            if (!validTokens.Any())
            {
                _logger.LogWarning("No valid tokens found for the requested symbols after filtering");
                return new List<StockData>();
            }

            foreach (var exchange in validTokens)
            {
                try
                {
                    _logger.LogInformation("Fetching {Count} quotes from {Exchange} in a single API call",
                        exchange.Value.Count, exchange.Key);

                    var quotes = await ExecuteMarketDataRequestAsync(
                        $"angelone_bulk_{exchange.Key}_{DateTime.Now.Ticks}",
                        QUOTE_CACHE_MINUTES,
                        async (client) =>
                        {
                            await ConfigureClientHeadersAsync(client, true, true);
                            var requestBody = new
                            {
                                mode = "FULL",
                                exchangeTokens = new Dictionary<string, List<string>>
                                {
                                    { exchange.Key, exchange.Value }
                                }
                            };

                            return await FetchMarketDataAsync(client, requestBody);
                        });

                    _logger.LogInformation("✅ Successfully fetched {Count} quotes from {Exchange}", quotes.Count, exchange.Key);

                    foreach (var quote in quotes)
                    {
                        var key = $"{quote.Exchange}_{quote.SymbolToken}";
                        if (symbolToTokenMap.TryGetValue(key, out var originalSymbol))
                        {
                            var stockData = MapMarketQuoteToStockData(quote, exchange.Key);
                            if (stockData != null && stockData.Price > 0)
                            {
                                results.Add(stockData);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching bulk quotes for {Exchange}", exchange.Key);
                }
            }

            _logger.LogInformation("Total quotes fetched: {Count} out of {Total} valid symbols requested",
                results.Count, filteredSymbols.Count);

            return results;
        }

        public async Task<MarketIndices?> GetIndicesAsync()
        {
            await EnsureAuthenticatedAsync();

            return await ExecuteWithRetryAsync(async () =>
            {
                return await ExecuteMarketDataRequestAsync(
                    "angelone_indices",
                    INDICES_CACHE_MINUTES,
                    async (client) =>
                    {
                        await ConfigureClientHeadersAsync(client, true, true);
                        var requestBody = new
                        {
                            mode = "FULL",
                            exchangeTokens = new Dictionary<string, List<string>>
                            {
                                { "NSE", new List<string> { "9", "13", "10" } }
                            }
                        };

                        var quotes = await FetchMarketDataAsync(client, requestBody);
                        return MapIndicesResponse(quotes);
                    });
            }, "GetIndices");
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

                var symbolToken = GetSymbolToken(order.Symbol);

                var request = new
                {
                    variety = order.Variety ?? "NORMAL",
                    tradingsymbol = order.Symbol,
                    symboltoken = symbolToken,
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

                return await ExecuteOrderRequestAsync(AngelOneConstants.ORDER_ENDPOINT, request);
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

            var request = new { variety = "NORMAL", orderid = orderId };
            var response = await ExecuteOrderRequestAsync(AngelOneConstants.CANCEL_ORDER_ENDPOINT, request);

            return response.Status == "SUCCESS";
        }

        public async Task<ExecutionResult> ExecuteTradeAsync(TradeAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
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

                return new ExecutionResult
                {
                    Success = response.Status == "SUCCESS",
                    OrderId = response.OrderId,
                    Symbol = action.Symbol,
                    Quantity = action.Quantity,
                    ExecutedPrice = action.Price,
                    Message = response.Message,
                    ExecutionTime = DateTime.Now
                };
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

        #region Master Quote Operations

        /// <summary>
        /// Gets the complete list of stocks from Angel One scrip master (JSON file)
        /// This is the official and reliable source for all tradable symbols
        /// </summary>
        public async Task<List<AngelOneMasterQuote>> GetMasterQuoteAsync(string exchange = "NSE", CancellationToken cancellationToken = default)
        {
            var cacheKey = $"angelone_master_quote_{exchange}";

            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                try
                {
                    _logger.LogInformation("Fetching scrip master from Angel One for {Exchange}", exchange);

                    const string scripMasterUrl = "https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json";

                    using var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(30);

                    var response = await client.GetAsync(scripMasterUrl, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Failed to fetch scrip master. Status: {StatusCode}", response.StatusCode);
                        return new List<AngelOneMasterQuote>();
                    }

                    var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    var scrips = JsonSerializer.Deserialize<List<ScripMasterEntry>>(jsonContent,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (scrips == null || !scrips.Any())
                    {
                        _logger.LogWarning("No scrips found in master file");
                        return new List<AngelOneMasterQuote>();
                    }

                    _logger.LogInformation("Loaded {Total} total scrips from master file", scrips.Count);

                    var quotes = new List<AngelOneMasterQuote>();

                    foreach (var scrip in scrips)
                    {
                        bool shouldInclude = false;

                        if (exchange == "NSE")
                        {
                            var instType = scrip.instrumenttype?.ToUpperInvariant() ?? "";
                            // Equity stocks have empty instrument type, indices have "AMXIDX"
                            shouldInclude = scrip.exch_seg == "NSE" &&
                                           (string.IsNullOrEmpty(instType) ||
                                            instType == "EQ" ||
                                            instType == "EQUITY" ||
                                            instType == "AMXIDX");
                        }
                        else if (exchange == "BSE")
                        {
                            shouldInclude = scrip.exch_seg == "BSE" && scrip.instrumenttype?.ToUpperInvariant() == "EQ";
                        }
                        else if (exchange == "NFO")
                        {
                            shouldInclude = scrip.exch_seg == "NFO";
                        }
                        else if (exchange == "MCX")
                        {
                            shouldInclude = scrip.exch_seg == "MCX";
                        }

                        if (shouldInclude && !string.IsNullOrEmpty(scrip.symbol))
                        {
                            quotes.Add(new AngelOneMasterQuote
                            {
                                Symbol = scrip.symbol.Replace(" ", ""),
                                TradingSymbol = scrip.symbol,
                                CompanyName = scrip.name,
                                Token = scrip.token,
                                Exchange = scrip.exch_seg,
                                InstrumentType = string.IsNullOrEmpty(scrip.instrumenttype) ? "EQ" : scrip.instrumenttype,
                                LotSize = scrip.lotsize,
                                Strike = scrip.strike,
                                Expiry = scrip.expiry
                            });
                        }
                    }

                    var eqCount = quotes.Count(q => q.InstrumentType?.ToUpperInvariant() == "EQ");
                    var indexCount = quotes.Count(q => q.InstrumentType?.ToUpperInvariant() == "AMXIDX");
                    _logger.LogInformation("✅ Successfully fetched {Total} stocks from scrip master for {Exchange} (EQ: {EqCount}, Indices: {IndexCount})",
                        quotes.Count, exchange, eqCount, indexCount);

                    return quotes;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching scrip master");
                    return new List<AngelOneMasterQuote>();
                }
            }, TimeSpan.FromHours(24));
        }

        /// <summary>
        /// Gets all master quotes for multiple exchanges
        /// </summary>
        public async Task<Dictionary<string, List<AngelOneMasterQuote>>> GetAllMasterQuotesAsync()
        {
            var result = new Dictionary<string, List<AngelOneMasterQuote>>();
            var exchanges = new[] { "NSE", "BSE" };

            foreach (var exchange in exchanges)
            {
                var quotes = await GetMasterQuoteAsync(exchange);
                if (quotes.Any())
                {
                    result[exchange] = quotes;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets equity stocks only (for trading)
        /// </summary>
        public async Task<List<AngelOneMasterQuote>> GetEquityStocksAsync(string exchange = "NSE")
        {
            var allStocks = await GetMasterQuoteAsync(exchange);
            return allStocks.Where(s => s.InstrumentType == "EQ").ToList();
        }

        /// <summary>
        /// Gets top Nifty 50 stocks
        /// </summary>
        public async Task<List<AngelOneMasterQuote>> GetNifty50StocksAsync()
        {
            var allStocks = await GetMasterQuoteAsync("NSE");
            return allStocks.Where(s => s.InstrumentType == "EQ")
                            .Take(50)
                            .ToList();
        }

        #endregion

        #region Private Helper Methods
        
        private async Task<OrderResponse> ExecuteOrderRequestAsync(string endpoint, object requestBody)
        {
            using var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(AngelOneConstants.BASE_URL);
            client.Timeout = TimeSpan.FromSeconds(30);

            await ConfigureClientHeadersAsync(client, true, false);

            var response = await client.PostAsync(endpoint, CreateJsonContent(requestBody));
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<AngelOneOrderResponse>(responseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.data != null)
                {
                    _logger.LogInformation("✅ Order {OrderId} processed successfully", result.data.orderid);

                    return new OrderResponse
                    {
                        OrderId = result.data.orderid,
                        Status = "SUCCESS",
                        Message = "Order placed successfully",
                        OrderTime = DateTime.Now
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

        private async Task<List<Holding>> HandleHoldingsResponseAsync(HttpResponseMessage response)
        {
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<AngelOneHoldingsResponse>(responseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.status == true && result.data != null)
                {
                    var holdings = result.data.Select(h => new Holding
                    {
                        Symbol = h.tradingsymbol,
                        Quantity = h.quantity + h.t1quantity,
                        AveragePrice = h.averageprice,
                        CurrentPrice = h.ltp,
                        ProfitLoss = h.profitloss ?? h.profitandloss
                    }).ToList();

                    _logger.LogInformation("✅ Successfully fetched {Count} holdings", holdings.Count);
                    return holdings;
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Token expired, re-authenticating...");
                lock (typeof(AngelOneService))
                {
                    _globalAccessToken = string.Empty;
                    _globalTokenExpiry = DateTime.MinValue;
                }
                await EnsureAuthenticatedAsync();
                return await GetHoldingsAsync();
            }
            else
            {
                _logger.LogError("❌ Holdings request failed: {StatusCode}", response.StatusCode);
                _logger.LogError("Response: {Response}", responseContent);
            }

            return new List<Holding>();
        }
                
        #endregion
    }

    internal record AngelOneCredentials(string ApiKey, string ClientId, string Mpin, string TotpSecret);
}