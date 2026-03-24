// Services/NseApiService.cs
using System.Net;
using System.Text;
using System.Text.Json;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class NseApiService : INseApiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NseApiService> _logger;
        private readonly ICircuitBreakerService _circuitBreaker;
        private readonly SemaphoreSlim _sessionLock = new(1, 1);

        private string? _sessionCookie;
        private DateTime _cookieExpiry = DateTime.MinValue;
        private readonly HttpClient _httpClient;

        // NSE endpoints
        private const string NSE_HOMEPAGE = "https://www.nseindia.com";
        private const string NSE_QUOTE_API = "https://www.nseindia.com/api/quote-equity?symbol={0}";
        private const string NSE_MASTER_QUOTE = "https://www.nseindia.com/api/master-quote";

        // Rate limiting
        private static readonly Queue<DateTime> _requestTimestamps = new();
        private const int MAX_REQUESTS_PER_MINUTE = 20;
        private readonly SemaphoreSlim _rateLimitLock = new(1, 1);

        public NseApiService(
            IHttpClientFactory httpClientFactory,
            ILogger<NseApiService> logger,
            ICircuitBreakerService circuitBreaker)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _circuitBreaker = circuitBreaker;
            _httpClient = CreateHttpClient();
        }

        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                UseCookies = true,
                AllowAutoRedirect = true,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            var client = new HttpClient(handler);

            // Set browser-like headers
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
            client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
            client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");

            client.Timeout = TimeSpan.FromSeconds(30);

            return client;
        }

        public async Task<NSEQuoteResponse?> GetQuoteAsync(string symbol)
        {
            return await _circuitBreaker.ExecuteAsync("NSE", async () =>
            {
                await EnsureRateLimitAsync();

                if (!await EnsureSessionAsync())
                {
                    _logger.LogWarning("Failed to establish NSE session for {Symbol}", symbol);
                    return null;
                }

                try
                {
                    var url = string.Format(NSE_QUOTE_API, symbol.ToUpper());
                    _logger.LogDebug("Fetching NSE quote for {Symbol}", symbol);

                    var response = await _httpClient.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("NSE API returned {StatusCode} for {Symbol}", response.StatusCode, symbol);

                        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode == 429)
                        {
                            _logger.LogWarning("NSE rate limit hit for {Symbol}", symbol);
                            await Task.Delay(5000);
                        }

                        return null;
                    }

                    var content = await response.Content.ReadAsStringAsync();

                    // Validate response
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogWarning("NSE returned empty response for {Symbol}", symbol);
                        return null;
                    }

                    // Check if it's HTML (error page)
                    if (content.Contains("<html") || content.Contains("<HTML"))
                    {
                        _logger.LogWarning("NSE returned HTML page for {Symbol} - session may be expired", symbol);
                        _sessionCookie = null; // Force session refresh
                        return null;
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };

                    try
                    {
                        var quote = JsonSerializer.Deserialize<NSEQuoteResponse>(content, options);

                        if (quote?.PriceInfo == null)
                        {
                            _logger.LogWarning("NSE quote missing PriceInfo for {Symbol}", symbol);
                            return null;
                        }

                        _logger.LogDebug("Successfully fetched NSE quote for {Symbol}", symbol);
                        return quote;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse NSE JSON for {Symbol}. First 200 chars: {Content}",
                            symbol, content.Length > 200 ? content.Substring(0, 200) : content);
                        return null;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "HTTP error fetching NSE quote for {Symbol}", symbol);
                    throw; // Let circuit breaker handle it
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Timeout fetching NSE quote for {Symbol}", symbol);
                    throw; // Let circuit breaker handle it
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error fetching NSE quote for {Symbol}", symbol);
                    throw;
                }
            }, null);
        }

        public async Task<NSEQuoteResponse?> GetQuoteWithRetryAsync(string symbol, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var quote = await GetQuoteAsync(symbol);
                    if (quote != null)
                        return quote;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed for {Symbol}",
                        i + 1, maxRetries, symbol);
                }

                if (i < maxRetries - 1)
                {
                    // Exponential backoff
                    await Task.Delay(1000 * (int)Math.Pow(2, i));
                }
            }

            return null;
        }

        public async Task<List<string>> GetSymbolsAsync()
        {
            return await _circuitBreaker.ExecuteAsync("NSE", async () =>
            {
                await EnsureRateLimitAsync();

                if (!await EnsureSessionAsync())
                {
                    _logger.LogWarning("Failed to establish NSE session for symbols fetch");
                    return new List<string>();
                }

                try
                {
                    _logger.LogDebug("Fetching NSE master quote symbols");

                    var response = await _httpClient.GetAsync(NSE_MASTER_QUOTE);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("NSE master quote API returned {StatusCode}", response.StatusCode);
                        return new List<string>();
                    }

                    var content = await response.Content.ReadAsStringAsync();

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogWarning("NSE master quote returned empty response");
                        return new List<string>();
                    }

                    var symbols = JsonSerializer.Deserialize<List<string>>(content);

                    _logger.LogInformation("Successfully fetched {Count} symbols from NSE", symbols?.Count ?? 0);

                    return symbols ?? new List<string>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching NSE symbols");
                    throw;
                }
            }, new List<string>());
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                _logger.LogInformation("Testing NSE API connection...");

                var symbols = await GetSymbolsAsync();
                var success = symbols?.Any() == true;

                _logger.LogInformation("NSE API connection test: {Result}", success ? "SUCCESS" : "FAILED");

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NSE API connection test failed");
                return false;
            }
        }

        #region Private Methods

        private async Task<bool> EnsureSessionAsync()
        {
            await _sessionLock.WaitAsync();
            try
            {
                // Check if we have a valid session
                if (!string.IsNullOrEmpty(_sessionCookie) && DateTime.UtcNow < _cookieExpiry)
                {
                    return true;
                }

                _logger.LogInformation("Establishing new NSE session...");

                using var client = new HttpClient(new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseCookies = true,
                    AllowAutoRedirect = true
                });

                // Set headers
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");

                // Visit homepage to get cookies
                _logger.LogDebug("Visiting NSE homepage...");
                var response = await client.GetAsync(NSE_HOMEPAGE);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to access NSE homepage: {StatusCode}", response.StatusCode);
                    return false;
                }

                // Read the HTML to ensure session is established
                var html = await response.Content.ReadAsStringAsync();

                // Extract cookies from response headers
                var cookieStrings = new List<string>();

                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    foreach (var cookieHeader in cookies)
                    {
                        var cookieParts = cookieHeader.Split(';');
                        if (cookieParts.Length > 0)
                        {
                            cookieStrings.Add(cookieParts[0]);
                        }
                    }
                }

                // If no cookies in first attempt, try again with a delay
                if (!cookieStrings.Any())
                {
                    _logger.LogDebug("No cookies received, waiting and retrying...");
                    await Task.Delay(2000);

                    response = await client.GetAsync(NSE_HOMEPAGE);

                    if (response.Headers.TryGetValues("Set-Cookie", out var retryCookies))
                    {
                        foreach (var cookieHeader in retryCookies)
                        {
                            var cookieParts = cookieHeader.Split(';');
                            if (cookieParts.Length > 0)
                            {
                                cookieStrings.Add(cookieParts[0]);
                            }
                        }
                    }
                }

                if (cookieStrings.Any())
                {
                    _sessionCookie = string.Join("; ", cookieStrings.Distinct());

                    // Set cookie for main client
                    _httpClient.DefaultRequestHeaders.Remove("Cookie");
                    _httpClient.DefaultRequestHeaders.Add("Cookie", _sessionCookie);

                    _cookieExpiry = DateTime.UtcNow.AddMinutes(25); // NSE cookies typically last 30 mins

                    // Visit additional endpoints to ensure session is fully active
                    await Task.Delay(1000);

                    try
                    {
                        await client.GetAsync("https://www.nseindia.com/api/marketStatus");
                    }
                    catch { /* Ignore errors from additional endpoints */ }

                    _logger.LogInformation("NSE session established successfully. Cookie expires at {Expiry}", _cookieExpiry);
                    return true;
                }

                _logger.LogWarning("Failed to establish NSE session - no cookies received");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error establishing NSE session");
                return false;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task EnsureRateLimitAsync()
        {
            await _rateLimitLock.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;

                // Remove timestamps older than 1 minute
                while (_requestTimestamps.Any() && _requestTimestamps.Peek() < now.AddMinutes(-1))
                {
                    _requestTimestamps.Dequeue();
                }

                if (_requestTimestamps.Count >= MAX_REQUESTS_PER_MINUTE)
                {
                    var oldest = _requestTimestamps.Peek();
                    var waitTime = (int)(oldest.AddMinutes(1) - now).TotalMilliseconds;

                    if (waitTime > 0)
                    {
                        _logger.LogDebug("Rate limit reached. Waiting {WaitTime}ms", waitTime);
                        await Task.Delay(waitTime + 100); // Add small buffer
                    }
                }

                _requestTimestamps.Enqueue(now);
            }
            finally
            {
                _rateLimitLock.Release();
            }
        }

        #endregion

        public void Dispose()
        {
            _sessionLock?.Dispose();
            _rateLimitLock?.Dispose();
            _httpClient?.Dispose();
        }
    }
}