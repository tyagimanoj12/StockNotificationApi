//using StockNotificationApi.Models;
//using System.Text.Json;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public partial class AngelOneService
//    {
//        public async Task<List<Holding>> GetHoldingsAsync()
//        {
//            await EnsureAuthenticatedAsync();
//            return await ExecuteAuthenticatedRequestAsync(
//                $"angelone_holdings_{await GetUserIdAsync()}",
//                HOLDINGS_CACHE_MINUTES,
//                async (client) =>
//                {
//                    await ConfigureClientHeadersAsync(client, true, false);
//                    var request = new HttpRequestMessage(HttpMethod.Get, HOLDINGS_ENDPOINT);
//                    var response = await client.SendAsync(request);
//                    return await HandleHoldingsResponseAsync(response);
//                });
//        }

//        public async Task<List<Position>> GetPositionsAsync()
//        {
//            await EnsureAuthenticatedAsync();
//            return await ExecuteAuthenticatedRequestAsync(
//                $"angelone_positions_{await GetUserIdAsync()}",
//                HOLDINGS_CACHE_MINUTES,
//                async (client) =>
//                {
//                    await ConfigureClientHeadersAsync(client, true, false);
//                    var endpoints = new[] { POSITIONS_ENDPOINT, POSITIONS_ENDPOINT_2, POSITIONS_ENDPOINT_3 };

//                    foreach (var endpoint in endpoints)
//                    {
//                        var positions = await TryGetPositionsFromEndpointAsync(client, endpoint);
//                        if (positions != null) return positions;
//                    }

//                    return new List<Position>();
//                });
//        }

//        private async Task<List<Position>?> TryGetPositionsFromEndpointAsync(HttpClient client, string endpoint)
//        {
//            try
//            {
//                HttpResponseMessage response;

//                if (endpoint.Contains("getAllPositions"))
//                {
//                    var requestBody = new { clientcode = await GetUserIdAsync() ?? _configuration["AngelOne:ClientId"] };
//                    response = await client.PostAsync(endpoint, CreateJsonContent(requestBody));
//                }
//                else
//                {
//                    response = await client.GetAsync(endpoint);
//                }

//                var responseContent = await response.Content.ReadAsStringAsync();

//                if (response.IsSuccessStatusCode)
//                {
//                    var result = JsonSerializer.Deserialize<AngelOnePositionsResponse>(responseContent,
//                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//                    if (result?.status == true && result.data != null)
//                    {
//                        return result.data.Select(p => new Position
//                        {
//                            Symbol = p.tradingsymbol,
//                            Quantity = p.quantity,
//                            BuyPrice = p.buyprice,
//                            CurrentPrice = p.ltp
//                        }).ToList();
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning(ex, "Error with endpoint {Endpoint}", endpoint);
//            }

//            return null;
//        }

//        public async Task<PortfolioSummary> GetPortfolioAsync()
//        {
//            return await ExecuteAuthenticatedRequestAsync(
//                $"angelone_portfolio_{await GetUserIdAsync()}",
//                PORTFOLIO_CACHE_MINUTES,
//                async (_) =>
//                {
//                    var holdings = await GetHoldingsAsync();

//                    var summary = new PortfolioSummary
//                    {
//                        TotalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice),
//                        CurrentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice),
//                        Holdings = holdings,
//                        AsOfDate = DateTime.Now
//                    };

//                    var sectorAllocation = new Dictionary<string, decimal>();
//                    foreach (var holding in holdings)
//                    {
//                        var sector = GetSectorFromSymbol(holding.Symbol);
//                        sectorAllocation[sector] = sectorAllocation.GetValueOrDefault(sector) +
//                                                  (holding.Quantity * holding.CurrentPrice);
//                    }

//                    if (summary.CurrentValue > 0)
//                    {
//                        summary.SectorAllocation = sectorAllocation.ToDictionary(
//                            kv => kv.Key,
//                            kv => (kv.Value / summary.CurrentValue) * 100);
//                    }

//                    return summary;
//                });
//        }

//        private async Task<List<Holding>> HandleHoldingsResponseAsync(HttpResponseMessage response)
//        {
//            var responseContent = await response.Content.ReadAsStringAsync();

//            if (response.IsSuccessStatusCode)
//            {
//                var result = JsonSerializer.Deserialize<AngelOneHoldingsResponse>(responseContent,
//                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//                if (result?.status == true && result.data != null)
//                {
//                    var holdings = result.data.Select(h => new Holding
//                    {
//                        Symbol = h.tradingsymbol,
//                        Quantity = h.quantity + h.t1quantity,
//                        AveragePrice = h.averageprice,
//                        CurrentPrice = h.ltp,
//                        ProfitLoss = h.profitloss ?? h.profitandloss
//                    }).ToList();

//                    _logger.LogInformation("✅ Successfully fetched {Count} holdings", holdings.Count);
//                    return holdings;
//                }
//            }
//            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
//            {
//                _logger.LogWarning("Token expired, re-authenticating...");
//                lock (typeof(AngelOneService))
//                {
//                    _globalAccessToken = string.Empty;
//                    _globalTokenExpiry = DateTime.MinValue;
//                }
//                await EnsureAuthenticatedAsync();
//                return await GetHoldingsAsync();
//            }
//            else
//            {
//                _logger.LogError("❌ Holdings request failed: {StatusCode}", response.StatusCode);
//                _logger.LogError("Response: {Response}", responseContent);
//            }

//            return new List<Holding>();
//        }
//    }
//}