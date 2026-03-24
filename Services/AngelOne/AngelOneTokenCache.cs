//using StockNotificationApi.Interfaces;
//using StockNotificationApi.Services.AngelOne.Models;

//namespace StockNotificationApi.Services.AngelOne
//{
//    public class AngelOneTokenCache
//    {
//        private static Dictionary<string, string> _symbolTokenCache = new(StringComparer.OrdinalIgnoreCase);
//        private static readonly SemaphoreSlim _tokenCacheLock = new(1, 1);
//        private static DateTime _tokenCacheExpiry = DateTime.MinValue;

//        private readonly ILogger<AngelOneTokenCache> _logger;
//        private readonly IAngelOneService _angelOneService;

//        public AngelOneTokenCache(
//            ILogger<AngelOneTokenCache> logger,
//            IAngelOneService angelOneService)
//        {
//            _logger = logger;
//            _angelOneService = angelOneService;
//        }

//        public async Task<string?> GetSymbolTokenAsync(string symbol)
//        {
//            if (string.IsNullOrEmpty(symbol)) return null;

//            var cleanSymbol = CleanSymbol(symbol).ToUpperInvariant();

//            await _tokenCacheLock.WaitAsync();
//            try
//            {
//                if (_tokenCacheExpiry < DateTime.UtcNow || _symbolTokenCache.Count == 0)
//                {
//                    await RefreshTokenCacheAsync();
//                }

//                if (_symbolTokenCache.TryGetValue(cleanSymbol, out var token))
//                {
//                    return token;
//                }

//                var withoutSuffix = cleanSymbol.Replace("-EQ", "").Replace(".NS", "").Replace(".NSE", "").Trim();
//                if (withoutSuffix != cleanSymbol && _symbolTokenCache.TryGetValue(withoutSuffix, out token))
//                {
//                    return token;
//                }

//                var partialMatch = _symbolTokenCache
//                    .Where(kv => kv.Key.Contains(cleanSymbol) || cleanSymbol.Contains(kv.Key))
//                    .Select(kv => kv.Value)
//                    .FirstOrDefault();

//                if (partialMatch != null)
//                {
//                    _logger.LogDebug("Partial match found for {Symbol} -> Token {Token}", symbol, partialMatch);
//                    return partialMatch;
//                }
//            }
//            finally
//            {
//                _tokenCacheLock.Release();
//            }

//            _logger.LogDebug("Token not found for symbol: {Symbol}", symbol);
//            return null;
//        }

//        public async Task RefreshTokenCacheAsync()
//        {
//            try
//            {
//                _logger.LogInformation("Refreshing symbol token cache from scrip master...");

//                var masterQuotes = await _angelOneService.GetMasterQuoteAsync("NSE");

//                var newCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

//                foreach (var quote in masterQuotes)
//                {
//                    if ((quote.InstrumentType == "EQ" || quote.InstrumentType == "AMXIDX") && !string.IsNullOrEmpty(quote.Token))
//                    {
//                        var symbol = quote.Symbol.Replace("-EQ", "").Trim();
//                        if (!newCache.ContainsKey(symbol) && !string.IsNullOrEmpty(symbol))
//                        {
//                            newCache[symbol] = quote.Token;
//                        }

//                        var tradingSymbol = quote.TradingSymbol?.Replace("-EQ", "").Trim();
//                        if (!string.IsNullOrEmpty(tradingSymbol) && tradingSymbol != symbol && !newCache.ContainsKey(tradingSymbol))
//                        {
//                            newCache[tradingSymbol] = quote.Token;
//                        }
//                    }
//                }

//                // Add index tokens
//                newCache["NIFTY"] = "9";
//                newCache["NIFTY50"] = "9";
//                newCache["NIFTY 50"] = "9";
//                newCache["BANKNIFTY"] = "13";
//                newCache["SENSEX"] = "10";
//                newCache["NIFTYBEES"] = "5893";
//                newCache["JUNIORBEES"] = "4565";
//                newCache["BANKBEES"] = "189";
//                newCache["ITBEES"] = "4244";

//                _symbolTokenCache = newCache;
//                _tokenCacheExpiry = DateTime.UtcNow.AddHours(24);

//                _logger.LogInformation("Token cache refreshed with {Count} symbols", _symbolTokenCache.Count);
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Failed to refresh token cache");
//            }
//        }

//        private string CleanSymbol(string symbol)
//        {
//            return symbol.Replace(".NS", "").Replace(".NSE", "").Replace(".BO", "").Replace(".BSE", "").Trim();
//        }

//        public void ClearCache()
//        {
//            lock (_symbolTokenCache)
//            {
//                _symbolTokenCache.Clear();
//                _tokenCacheExpiry = DateTime.MinValue;
//            }
//        }
//    }
//}