//using StockNotificationApi.Services.AngelOne.Models;
//using System.Text.Json;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public partial class AngelOneService
//    {
//        public async Task<List<AngelOneMasterQuote>> GetMasterQuoteAsync(string exchange = "NSE", CancellationToken cancellationToken = default)
//        {
//            var cacheKey = $"angelone_master_quote_{exchange}";

//            return await _cache.GetOrSetAsync(cacheKey, async () =>
//            {
//                try
//                {
//                    _logger.LogInformation("Fetching scrip master from Angel One for {Exchange}", exchange);

//                    const string scripMasterUrl = "https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json";

//                    using var client = _httpClientFactory.CreateClient();
//                    client.Timeout = TimeSpan.FromSeconds(30);

//                    var response = await client.GetAsync(scripMasterUrl, cancellationToken);

//                    if (!response.IsSuccessStatusCode)
//                    {
//                        _logger.LogError("Failed to fetch scrip master. Status: {StatusCode}", response.StatusCode);
//                        return new List<AngelOneMasterQuote>();
//                    }

//                    var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
//                    var scrips = JsonSerializer.Deserialize<List<ScripMasterEntry>>(jsonContent,
//                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//                    if (scrips == null || !scrips.Any())
//                    {
//                        _logger.LogWarning("No scrips found in master file");
//                        return new List<AngelOneMasterQuote>();
//                    }

//                    _logger.LogInformation("Loaded {Total} total scrips from master file", scrips.Count);

//                    var quotes = new List<AngelOneMasterQuote>();

//                    foreach (var scrip in scrips)
//                    {
//                        bool shouldInclude = false;

//                        if (exchange == "NSE")
//                        {
//                            var instType = scrip.instrumenttype?.ToUpperInvariant() ?? "";
//                            shouldInclude = scrip.exch_seg == "NSE" &&
//                                           (string.IsNullOrEmpty(instType) ||
//                                            instType == "EQ" ||
//                                            instType == "EQUITY" ||
//                                            instType == "AMXIDX");
//                        }
//                        else if (exchange == "BSE")
//                        {
//                            shouldInclude = scrip.exch_seg == "BSE" && scrip.instrumenttype?.ToUpperInvariant() == "EQ";
//                        }
//                        else if (exchange == "NFO")
//                        {
//                            shouldInclude = scrip.exch_seg == "NFO";
//                        }
//                        else if (exchange == "MCX")
//                        {
//                            shouldInclude = scrip.exch_seg == "MCX";
//                        }

//                        if (shouldInclude && !string.IsNullOrEmpty(scrip.symbol))
//                        {
//                            quotes.Add(new AngelOneMasterQuote
//                            {
//                                Symbol = scrip.symbol.Replace(" ", ""),
//                                TradingSymbol = scrip.symbol,
//                                CompanyName = scrip.name,
//                                Token = scrip.token,
//                                Exchange = scrip.exch_seg,
//                                InstrumentType = string.IsNullOrEmpty(scrip.instrumenttype) ? "EQ" : scrip.instrumenttype,
//                                LotSize = scrip.lotsize,
//                                Strike = scrip.strike,
//                                Expiry = scrip.expiry
//                            });
//                        }
//                    }

//                    _logger.LogInformation("✅ Successfully fetched {Total} stocks from scrip master for {Exchange}",
//                        quotes.Count, exchange);

//                    return quotes;
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError(ex, "Error fetching scrip master");
//                    return new List<AngelOneMasterQuote>();
//                }
//            }, TimeSpan.FromHours(24));
//        }

//        public async Task<Dictionary<string, List<AngelOneMasterQuote>>> GetAllMasterQuotesAsync()
//        {
//            var result = new Dictionary<string, List<AngelOneMasterQuote>>();
//            var exchanges = new[] { "NSE", "BSE" };

//            foreach (var exchange in exchanges)
//            {
//                var quotes = await GetMasterQuoteAsync(exchange);
//                if (quotes.Any())
//                {
//                    result[exchange] = quotes;
//                }
//            }

//            return result;
//        }

//        public async Task<List<AngelOneMasterQuote>> GetEquityStocksAsync(string exchange = "NSE")
//        {
//            var allStocks = await GetMasterQuoteAsync(exchange);
//            return allStocks.Where(s => s.InstrumentType == "EQ").ToList();
//        }

//        public async Task<List<AngelOneMasterQuote>> GetNifty50StocksAsync()
//        {
//            var allStocks = await GetMasterQuoteAsync("NSE");
//            return allStocks.Where(s => s.InstrumentType == "EQ")
//                            .Take(50)
//                            .ToList();
//        }
//    }
//}