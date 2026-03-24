//using StockNotificationApi.Models;
//using System.Text.Json;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public partial class AngelOneService
//    {
//        public async Task<StockData?> GetLiveQuoteAsync(string symbol)
//        {
//            if (string.IsNullOrWhiteSpace(symbol)) return null;

//            await EnsureAuthenticatedAsync();

//            return await ExecuteWithRetryAsync(async () =>
//            {
//                var cleanSymbol = CleanSymbol(symbol);
//                var symbolToken = await _tokenCache.GetSymbolTokenAsync(cleanSymbol);

//                if (string.IsNullOrEmpty(symbolToken)) return null;

//                return await ExecuteMarketDataRequestAsync(
//                    $"angelone_quote_{cleanSymbol}",
//                    QUOTE_CACHE_MINUTES,
//                    async (client) =>
//                    {
//                        await ConfigureClientHeadersAsync(client, true, true);
//                        var requestBody = new
//                        {
//                            mode = "FULL",
//                            exchangeTokens = new Dictionary<string, List<string>>
//                            {
//                                { "NSE", new List<string> { symbolToken } }
//                            }
//                        };

//                        var quotes = await FetchMarketDataAsync(client, requestBody);
//                        var quote = quotes?.FirstOrDefault();
//                        return quote != null ? MapMarketQuoteToStockData(quote, "NSE") : null;
//                    });
//            }, $"GetLiveQuote_{symbol}");
//        }

//        public async Task<List<StockData>> GetMultipleQuotesAsync(List<string> symbols)
//        {
//            if (symbols == null || !symbols.Any()) return new List<StockData>();

//            // Filter out ETFs/MFs
//            var validSymbols = new List<string>();
//            var filteredOut = new List<string>();

//            foreach (var symbol in symbols)
//            {
//                var cleanSymbol = CleanSymbol(symbol);
//                if (IsETFOrMutualFund(cleanSymbol))
//                {
//                    filteredOut.Add(symbol);
//                }
//                else
//                {
//                    validSymbols.Add(symbol);
//                }
//            }

//            if (filteredOut.Any())
//            {
//                _logger.LogInformation("Filtered out {Count} ETF/MF symbols", filteredOut.Count);
//            }

//            if (!validSymbols.Any())
//            {
//                _logger.LogWarning("All symbols filtered out as ETF/MF, returning empty list");
//                return new List<StockData>();
//            }

//            _logger.LogInformation("Fetching quotes for {Count} valid symbols", validSymbols.Count);
//            await EnsureAuthenticatedAsync();

//            // Build symbol to token mapping
//            var symbolToToken = new Dictionary<string, string>();
//            var symbolsWithTokens = new List<string>();

//            foreach (var symbol in validSymbols)
//            {
//                var cleanSymbol = CleanSymbol(symbol);
//                var token = await _tokenCache.GetSymbolTokenAsync(cleanSymbol);

//                if (!string.IsNullOrEmpty(token))
//                {
//                    symbolToToken[symbol] = token;
//                    symbolsWithTokens.Add(symbol);
//                }
//            }

//            if (!symbolsWithTokens.Any())
//            {
//                _logger.LogWarning("No symbols with valid tokens found");
//                return new List<StockData>();
//            }

//            // Group by exchange and fetch in batches
//            var results = new List<StockData>();
//            var exchangeTokens = new Dictionary<string, List<string>>();
//            var tokenToSymbol = new Dictionary<string, string>();

//            foreach (var symbol in symbolsWithTokens)
//            {
//                var exchange = DetermineExchange(symbol);
//                var token = symbolToToken[symbol];

//                if (!exchangeTokens.ContainsKey(exchange))
//                    exchangeTokens[exchange] = new List<string>();

//                exchangeTokens[exchange].Add(token);
//                tokenToSymbol[token] = symbol;
//            }

//            // Fetch quotes for each exchange in batches of 50
//            foreach (var exchange in exchangeTokens)
//            {
//                var batches = exchange.Value.Chunk(50);

//                foreach (var batch in batches)
//                {
//                    try
//                    {
//                        var quotes = await ExecuteMarketDataRequestAsync(
//                            $"angelone_bulk_{exchange.Key}_{DateTime.Now.Ticks}",
//                            QUOTE_CACHE_MINUTES,
//                            async (client) =>
//                            {
//                                await ConfigureClientHeadersAsync(client, true, true);
//                                var requestBody = new
//                                {
//                                    mode = "FULL",
//                                    exchangeTokens = new Dictionary<string, List<string>>
//                                    {
//                                        { exchange.Key, batch.ToList() }
//                                    }
//                                };

//                                return await FetchMarketDataAsync(client, requestBody);
//                            });

//                        foreach (var quote in quotes)
//                        {
//                            if (tokenToSymbol.TryGetValue(quote.SymbolToken, out var originalSymbol))
//                            {
//                                var stockData = MapMarketQuoteToStockData(quote, exchange.Key);
//                                if (stockData != null && stockData.Price > 0)
//                                {
//                                    results.Add(stockData);
//                                }
//                            }
//                        }
//                    }
//                    catch (Exception ex)
//                    {
//                        _logger.LogError(ex, "Error fetching batch for {Exchange}", exchange.Key);
//                    }
//                }
//            }

//            _logger.LogInformation("Total quotes fetched: {Count} out of {Total}", results.Count, symbolsWithTokens.Count);
//            return results;
//        }

//        public async Task<MarketIndices?> GetIndicesAsync()
//        {
//            await EnsureAuthenticatedAsync();

//            return await ExecuteWithRetryAsync(async () =>
//            {
//                return await ExecuteMarketDataRequestAsync(
//                    "angelone_indices",
//                    INDICES_CACHE_MINUTES,
//                    async (client) =>
//                    {
//                        await ConfigureClientHeadersAsync(client, true, true);
//                        var requestBody = new
//                        {
//                            mode = "FULL",
//                            exchangeTokens = new Dictionary<string, List<string>>
//                            {
//                                { "NSE", new List<string> { "9", "13", "10" } }
//                            }
//                        };

//                        var quotes = await FetchMarketDataAsync(client, requestBody);
//                        return MapIndicesResponse(quotes);
//                    });
//            }, "GetIndices");
//        }

//        private bool IsETFOrMutualFund(string symbol)
//        {
//            if (string.IsNullOrEmpty(symbol)) return true;

//            var upperSymbol = symbol.ToUpper().Trim();

//            if (AngelOneConstants.ValidStocksWithNumbers.Any(v => upperSymbol.Contains(v)))
//            {
//                return false;
//            }

//            if (AngelOneConstants.ClearETFPatterns.Any(p => upperSymbol.Contains(p)))
//            {
//                _logger.LogDebug("Filtered ETF: {Symbol}", symbol);
//                return true;
//            }

//            if (AngelOneConstants.InvalidSuffixes.Any(s => upperSymbol.EndsWith(s)))
//            {
//                _logger.LogDebug("Filtered by suffix: {Symbol}", symbol);
//                return true;
//            }

//            return false;
//        }
//    }
//}