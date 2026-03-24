//using OtpNet;
//using StockNotificationApi.Constants;
//using StockNotificationApi.Services.AngelOne.Models;
//using System.Text.Json;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public partial class AngelOneService
//    {
//        public async Task<AngelOneSession> AuthenticateAsync()
//        {
//            return await AuthenticateInternalAsync();
//        }

//        private async Task<AngelOneSession> AuthenticateInternalAsync()
//        {
//            if (await IsTokenValidAsync())
//            {
//                _logger.LogDebug("Token already valid, returning existing session");
//                return new AngelOneSession
//                {
//                    AuthToken = _globalAccessToken,
//                    UserId = _globalUserId,
//                    FeedToken = _globalFeedToken,
//                    ExpiresAt = _globalTokenExpiry
//                };
//            }

//            if (!await CanAttemptAuthAsync())
//            {
//                _logger.LogWarning("Authentication cooldown active");
//                return null;
//            }

//            await _globalAuthLock.WaitAsync();
//            try
//            {
//                if (await IsTokenValidAsync())
//                {
//                    return new AngelOneSession
//                    {
//                        AuthToken = _globalAccessToken,
//                        UserId = _globalUserId,
//                        FeedToken = _globalFeedToken,
//                        ExpiresAt = _globalTokenExpiry
//                    };
//                }

//                if (_isAuthenticating)
//                {
//                    _logger.LogWarning("Authentication already in progress, waiting...");
//                    var maxWait = TimeSpan.FromSeconds(30);
//                    var startTime = DateTime.UtcNow;

//                    while (_isAuthenticating && DateTime.UtcNow - startTime < maxWait)
//                    {
//                        await Task.Delay(100);
//                        if (await IsTokenValidAsync())
//                        {
//                            return new AngelOneSession
//                            {
//                                AuthToken = _globalAccessToken,
//                                UserId = _globalUserId,
//                                FeedToken = _globalFeedToken,
//                                ExpiresAt = _globalTokenExpiry
//                            };
//                        }
//                    }
//                }

//                _isAuthenticating = true;
//                await UpdateLastAuthAttemptAsync();

//                _logger.LogInformation("Authenticating with Angel One...");

//                var credentials = GetCredentials();
//                var totp = GenerateTOTP(credentials.TotpSecret);

//                var request = new
//                {
//                    clientcode = credentials.ClientId,
//                    password = credentials.Mpin,
//                    totp,
//                    api_key = credentials.ApiKey
//                };

//                await InitializeNetworkIdentifiersAsync();
//                await ConfigureClientHeadersAsync(_httpClient, false, false);
//                _httpClient.DefaultRequestHeaders.Add("X-PrivateKey", credentials.ApiKey);

//                var response = await _httpClient.PostAsync(AngelOneConstants.AUTH_ENDPOINT, CreateJsonContent(request));
//                var responseContent = await response.Content.ReadAsStringAsync();

//                if (response.IsSuccessStatusCode)
//                {
//                    return await ProcessAuthResponseAsync(responseContent);
//                }

//                await HandleAuthErrorAsync(response, responseContent);
//                return null;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Error authenticating with Angel One");
//                throw;
//            }
//            finally
//            {
//                _isAuthenticating = false;
//                _globalAuthLock.Release();
//            }
//        }

//        private async Task<bool> IsTokenValidAsync()
//        {
//            await Task.CompletedTask;
//            lock (typeof(AngelOneService))
//            {
//                return !string.IsNullOrEmpty(_globalAccessToken) &&
//                       _globalTokenExpiry > DateTime.UtcNow.AddMinutes(TimeConstants.TOKEN_EXPIRY_BUFFER_MINUTES);
//            }
//        }

//        private async Task<bool> CanAttemptAuthAsync()
//        {
//            await Task.CompletedTask;
//            lock (typeof(AngelOneService))
//            {
//                return (DateTime.UtcNow - _lastAuthAttempt).TotalSeconds >= AngelOneConstants.AUTH_COOLDOWN_SECONDS;
//            }
//        }

//        private async Task UpdateLastAuthAttemptAsync()
//        {
//            await Task.CompletedTask;
//            lock (typeof(AngelOneService))
//            {
//                _lastAuthAttempt = DateTime.UtcNow;
//            }
//        }

//        private async Task EnsureAuthenticatedAsync()
//        {
//            if (await IsTokenValidAsync()) return;
//            await AuthenticateInternalAsync();
//        }

//        private async Task<string> GetUserIdAsync()
//        {
//            await EnsureAuthenticatedAsync();
//            lock (typeof(AngelOneService))
//            {
//                return _globalUserId;
//            }
//        }

//        private AngelOneCredentials GetCredentials()
//        {
//            var apiKey = _configuration["AngelOne:ApiKey"];
//            var clientId = _configuration["AngelOne:ClientId"];
//            var mpin = _configuration["AngelOne:Mpin"];
//            var totpSecret = _configuration["AngelOne:TotpSecret"];

//            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(clientId) ||
//                string.IsNullOrEmpty(mpin) || string.IsNullOrEmpty(totpSecret))
//            {
//                throw new InvalidOperationException("Angel One credentials not configured");
//            }

//            return new AngelOneCredentials(apiKey, clientId, mpin, totpSecret);
//        }

//        private async Task InitializeNetworkIdentifiersAsync()
//        {
//            _clientLocalIP = await GetLocalIPAddressAsync();
//            _clientPublicIP = await GetPublicIPAsync();
//            _macAddress = GetMacAddress();

//            _logger.LogInformation("Using IPs - Local: {LocalIP}, Public: {PublicIP}, MAC: {MAC}",
//                _clientLocalIP, _clientPublicIP, _macAddress);
//        }

//        private async Task<AngelOneSession> ProcessAuthResponseAsync(string responseContent)
//        {
//            var result = JsonSerializer.Deserialize<AngelOneAuthResponse>(responseContent,
//                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//            if (result?.data == null)
//            {
//                _logger.LogError("Invalid auth response: no data");
//                return null;
//            }

//            var session = new AngelOneSession
//            {
//                AuthToken = result.data.jwtToken,
//                RefreshToken = result.data.refreshToken,
//                FeedToken = result.data.feedToken,
//                UserId = result.data.userId,
//                ExpiresAt = DateTime.UtcNow.AddHours(2)
//            };

//            lock (typeof(AngelOneService))
//            {
//                _globalAccessToken = result.data.jwtToken;
//                _globalUserId = result.data.userId;
//                _globalFeedToken = result.data.feedToken;
//                _globalTokenExpiry = session.ExpiresAt;
//            }

//            _logger.LogInformation("✅ Successfully authenticated with Angel One");
//            return session;
//        }

//        private async Task HandleAuthErrorAsync(HttpResponseMessage response, string responseContent)
//        {
//            await Task.CompletedTask;
//            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
//            {
//                _logger.LogError("❌ IP Address not whitelisted. Add this IP to Angel One portal: {Ip}", _clientPublicIP);
//            }
//            else
//            {
//                _logger.LogError("❌ Authentication failed: {StatusCode} - {Error}", response.StatusCode, responseContent);
//            }
//        }

//        private async Task ConfigureClientHeadersAsync(HttpClient client, bool includeAuth = true, bool isMarketData = false)
//        {
//            await Task.CompletedTask;
//            client.DefaultRequestHeaders.Clear();

//            if (includeAuth && !string.IsNullOrEmpty(_globalAccessToken))
//            {
//                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_globalAccessToken}");
//            }

//            client.DefaultRequestHeaders.Add("Accept", "application/json");
//            client.DefaultRequestHeaders.Add("X-UserType", "USER");
//            client.DefaultRequestHeaders.Add("X-SourceID", "WEB");
//            client.DefaultRequestHeaders.Add("X-ClientLocalIP", _clientLocalIP ?? "127.0.0.1");
//            client.DefaultRequestHeaders.Add("X-ClientPublicIP", _clientPublicIP ?? "127.0.0.1");
//            client.DefaultRequestHeaders.Add("X-MACAddress", _macAddress ?? "00-00-00-00-00-00");

//            var apiKey = _configuration["AngelOne:ApiKey"];
//            client.DefaultRequestHeaders.Add("X-PrivateKey", apiKey);

//            if (!string.IsNullOrEmpty(_globalFeedToken) && !isMarketData)
//            {
//                client.DefaultRequestHeaders.Add("X-FeedToken", _globalFeedToken);
//            }
//        }

//        private string GenerateTOTP(string secret)
//        {
//            var key = Base32Encoding.ToBytes(secret);
//            var totp = new Totp(key, step: 30, totpSize: 6);
//            return totp.ComputeTotp();
//        }

//        private async Task<string> GetPublicIPAsync()
//        {
//            try
//            {
//                using var client = new HttpClient();
//                client.Timeout = TimeSpan.FromSeconds(5);
//                return await client.GetStringAsync("https://api.ipify.org");
//            }
//            catch
//            {
//                return "127.0.0.1";
//            }
//        }

//        private async Task<string> GetLocalIPAddressAsync()
//        {
//            try
//            {
//                var hostName = System.Net.Dns.GetHostName();
//                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName);
//                var ipv4 = addresses.FirstOrDefault(a =>
//                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
//                    && !System.Net.IPAddress.IsLoopback(a));

//                return ipv4?.ToString() ?? "127.0.0.1";
//            }
//            catch
//            {
//                return "127.0.0.1";
//            }
//        }

//        private string GetMacAddress()
//        {
//            try
//            {
//                var mac = System.Net.NetworkInformation.NetworkInterface
//                    .GetAllNetworkInterfaces()
//                    .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
//                           && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
//                    .Select(nic => nic.GetPhysicalAddress().ToString())
//                    .FirstOrDefault();

//                return !string.IsNullOrEmpty(mac) ? mac : "00-00-00-00-00-00";
//            }
//            catch
//            {
//                return "00-00-00-00-00-00";
//            }
//        }
//    }
//}