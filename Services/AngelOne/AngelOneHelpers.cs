using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;

namespace StockNotificationApi.Services.AngelOne
{
    public partial class AngelOneService
    {
        private async Task<T> ExecuteAuthenticatedRequestAsync<T>(
            string cacheKey,
            int cacheMinutes,
            Func<HttpClient, Task<T>> action)
        {
            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(AngelOneConstants.BASE_URL);
                client.Timeout = TimeSpan.FromSeconds(30);

                await ConfigureClientHeadersAsync(client, true, false);
                return await action(client);
            }, TimeSpan.FromMinutes(cacheMinutes));
        }

        private async Task<T> ExecuteMarketDataRequestAsync<T>(
            string cacheKey,
            int cacheMinutes,
            Func<HttpClient, Task<T>> action)
        {
            await EnsureAuthenticatedAsync();

            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(AngelOneConstants.BASE_URL);
                client.Timeout = TimeSpan.FromSeconds(30);

                await ConfigureClientHeadersAsync(client, true, true);
                return await action(client);
            }, TimeSpan.FromMinutes(cacheMinutes));
        }

        private async Task<List<AngelOneMarketQuote>> FetchMarketDataAsync(HttpClient client, object requestBody)
        {
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, AngelOneConstants.QUOTE_ENDPOINT) { Content = content };

            var response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                try
                {
                    var quoteResponse = JsonSerializer.Deserialize<AngelOneMarketDataResponse>(responseContent, options);

                    if (quoteResponse?.Status == true && quoteResponse.Data?.Fetched != null)
                    {
                        return quoteResponse.Data.Fetched;
                    }

                    using var doc = JsonDocument.Parse(responseContent);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("data", out var dataElement) && dataElement.TryGetProperty("fetched", out var fetchedElement))
                    {
                        var fetchedJson = fetchedElement.GetRawText();
                        var quotes = JsonSerializer.Deserialize<List<AngelOneMarketQuote>>(fetchedJson, options);
                        if (quotes != null)
                            return quotes;
                    }

                    LogUnfetchedData(quoteResponse);
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogWarning(jsonEx, "Failed to parse Angel One response. First 200 chars: {Response}",
                        responseContent.Length > 200 ? responseContent.Substring(0, 200) : responseContent);
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
                return await FetchMarketDataAsync(client, requestBody);
            }
            else
            {
                _logger.LogError("❌ Market data request failed: {StatusCode}", response.StatusCode);
                _logger.LogError("Response: {Response}", responseContent);
            }

            return new List<AngelOneMarketQuote>();
        }

        private string CleanSymbol(string symbol)
        {
            return symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Replace(".BSE", "").Trim();
        }

        private string DetermineExchange(string symbol)
        {
            if (symbol.Contains(".BO") || symbol.Contains(".BSE")) return "BSE";
            if (symbol.Contains(".NFO")) return "NFO";
            if (symbol.Contains(".MCX")) return "MCX";
            return "NSE";
        }

        private StockData MapMarketQuoteToStockData(AngelOneMarketQuote quote, string exchange)
        {
            var cleanSymbol = quote.TradingSymbol?.Replace("-EQ", "") ?? string.Empty;

            return new StockData
            {
                Symbol = cleanSymbol,
                Name = quote.TradingSymbol?.Replace("-EQ", "") ?? cleanSymbol,
                Exchange = quote.Exchange ?? exchange,
                Price = quote.Ltp,
                Change = quote.NetChange,
                ChangePercent = quote.PercentChange,
                DayHigh = quote.High,
                DayLow = quote.Low,
                Open = quote.Open,
                PreviousClose = quote.Close,
                Volume = quote.TradeVolume,
                YearHigh = quote.YearHigh52,
                YearLow = quote.YearLow52,
                MarketCap = 0,
                PE = null,
                Sector = GetSectorFromSymbol(cleanSymbol),
                Industry = GetIndustryFromSymbol(cleanSymbol),
                Timestamp = DateTime.Now
            };
        }

        private MarketIndices MapIndicesResponse(List<AngelOneMarketQuote> quotes)
        {
            var indices = new MarketIndices();

            foreach (var quote in quotes)
            {
                var indexData = new IndexData
                {
                    Name = quote.TradingSymbol ?? "Unknown",
                    Value = quote.Ltp,
                    Change = quote.NetChange,
                    ChangePercent = quote.PercentChange,
                    Open = quote.Open,
                    DayHigh = quote.High,
                    DayLow = quote.Low
                };

                if (quote.SymbolToken == "9" || (quote.TradingSymbol?.Contains("NIFTY 50") == true))
                    indices.Nifty50 = indexData;
                else if (quote.SymbolToken == "13" || (quote.TradingSymbol?.Contains("BANK NIFTY") == true))
                    indices.BankNifty = indexData;
                else if (quote.SymbolToken == "10" || (quote.TradingSymbol?.Contains("SENSEX") == true))
                    indices.Sensex = indexData;
            }

            return indices;
        }

        private void LogUnfetchedData(AngelOneMarketDataResponse? response)
        {
            if (response?.Data?.Unfetched == null || !response.Data.Unfetched.Any()) return;

            foreach (var error in response.Data.Unfetched)
            {
                _logger.LogWarning("Unfetched: Exchange={Exchange}, Token={Token}, Message={Message}",
                    error.Exchange, error.SymbolToken, error.Message);
            }
        }

        private StringContent CreateJsonContent(object obj) =>
            new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

        private string GetSymbolToken(string symbol)
        {
            var token = SymbolTokenMap.GetAngelOneToken(symbol);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogDebug("Unknown symbol token for {Symbol}", symbol);
                return null;
            }
            return token;
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, string operationName, int maxRetries = 3)
        {
            int retryCount = 0;
            int delayMs = 1000;

            while (retryCount < maxRetries)
            {
                try
                {
                    return await action();
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError(ex, "❌ {Operation} failed after {Retries} retries", operationName, maxRetries);
                        throw;
                    }

                    _logger.LogWarning("Rate limit hit for {Operation}. Retry {Retry}/{MaxRetries} after {Delay}ms",
                        operationName, retryCount, maxRetries, delayMs);

                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
                catch (Exception ex) when (ex.Message.Contains("Unknown symbol token"))
                {
                    _logger.LogDebug("Skipping {Operation} - Symbol not found", operationName);
                    return default!;
                }
            }

            throw new InvalidOperationException($"All retries failed for {operationName}");
        }

        private string GetSectorFromSymbol(string symbol)
        {
            var upperSymbol = symbol.ToUpper().Replace("-EQ", "").Replace(".NS", "").Replace(".BO", "").Trim();

            return upperSymbol switch
            {
                "RELIANCE" => "Energy",
                "ONGC" => "Energy",
                "IOC" => "Energy",
                "BPCL" => "Energy",
                "GAIL" => "Energy",
                "TCS" => "Technology",
                "INFY" => "Technology",
                "HCLTECH" => "Technology",
                "TECHM" => "Technology",
                "WIPRO" => "Technology",
                "HDFCBANK" => "Banking",
                "ICICIBANK" => "Banking",
                "SBIN" => "Banking",
                "KOTAKBANK" => "Banking",
                "AXISBANK" => "Banking",
                "ITC" => "FMCG",
                "HINDUNILVR" => "FMCG",
                "BHARTIARTL" => "Telecom",
                "TATAMOTORS" => "Automobile",
                "MARUTI" => "Automobile",
                "SUNPHARMA" => "Pharma",
                _ => "Other"
            };
        }

        private string GetIndustryFromSymbol(string symbol)
        {
            var upperSymbol = symbol.ToUpper().Replace("-EQ", "").Replace(".NS", "").Replace(".BO", "").Trim();

            return upperSymbol switch
            {
                "RELIANCE" => "Oil & Gas",
                "TCS" => "IT Services",
                "INFY" => "IT Services",
                "HCLTECH" => "IT Services",
                "HDFCBANK" => "Private Bank",
                "ICICIBANK" => "Private Bank",
                "SBIN" => "Public Bank",
                "KOTAKBANK" => "Private Bank",
                "ITC" => "Diversified",
                "HINDUNILVR" => "Consumer Goods",
                "BHARTIARTL" => "Telecom Services",
                "TATAMOTORS" => "Automobile",
                "MARUTI" => "Automobile",
                "SUNPHARMA" => "Pharmaceuticals",
                _ => "General"
            };
        }
    }
}