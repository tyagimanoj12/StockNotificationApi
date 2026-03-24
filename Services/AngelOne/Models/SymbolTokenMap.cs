namespace StockNotificationApi.Services.AngelOne
{
    public static class SymbolTokenMap
    {
        private static readonly Dictionary<string, string> _tokenMap = new(StringComparer.OrdinalIgnoreCase);

        static SymbolTokenMap()
        {
            // Add mappings
            AddMapping("RELIANCE", "2885");
            AddMapping("TCS", "11536");
            AddMapping("HDFCBANK", "1333");
            AddMapping("INFY", "4083");
            AddMapping("HINDUNILVR", "2065");
            AddMapping("ICICIBANK", "467");
            AddMapping("ITC", "4245");
            AddMapping("SBIN", "779");
            AddMapping("BHARTIARTL", "259");
            AddMapping("KOTAKBANK", "4920");
            AddMapping("BAJFINANCE", "317");
            AddMapping("LT", "5144");
            AddMapping("WIPRO", "12106");
            AddMapping("AXISBANK", "151");
            AddMapping("TITAN", "10999");
            AddMapping("ASIANPAINT", "178");
            AddMapping("MARUTI", "5650");
            AddMapping("SUNPHARMA", "9840");
            AddMapping("HCLTECH", "1499");
            AddMapping("ULTRACEMCO", "10865");
            AddMapping("NTPC", "6705");
            AddMapping("POWERGRID", "7178");
            AddMapping("M&M", "5481");
            AddMapping("TATAMOTORS", "10325");
            AddMapping("TATASTEEL", "10421");
            AddMapping("JSWSTEEL", "4567");
            AddMapping("TECHM", "10275");
            AddMapping("INDUSINDBK", "5258");
            AddMapping("NESTLEIND", "5900");
            AddMapping("HDFC", "1329");
            AddMapping("BAJAJFINSV", "319");
            AddMapping("ONGC", "6733");
            AddMapping("ADANIPORTS", "47");
            AddMapping("GRASIM", "1269");
            AddMapping("DIVISLAB", "612");
            AddMapping("DRREDDY", "613");
            AddMapping("BPCL", "307");
            AddMapping("SHREECEM", "8295");
            AddMapping("COALINDIA", "371");
            AddMapping("EICHERMOT", "662");
            AddMapping("HEROMOTOCO", "1545");
            AddMapping("BRITANNIA", "309");
            AddMapping("UPL", "10902");
            AddMapping("CIPLA", "363");
            AddMapping("HINDALCO", "2051");
            AddMapping("APOLLOHOSP", "167");
            AddMapping("SBILIFE", "782");
            AddMapping("ICICIPRULI", "467");
            AddMapping("HDFCLIFE", "1329");
            AddMapping("BANKBARODA", "188");
            AddMapping("FEDERALBNK", "826");
            AddMapping("PNB", "6935");
            AddMapping("RBLBANK", "7350");
            AddMapping("ZOMATO", "12501");
            AddMapping("DMART", "617");
            AddMapping("TATACONSUM", "10415");
            AddMapping("VEDL", "11089");
            AddMapping("ADANIENT", "47");
            AddMapping("ADANIGREEN", "47");
            AddMapping("ADANITRANS", "47");
            AddMapping("IRCTC", "4392");
            AddMapping("LTI", "5150");
            AddMapping("MINDTREE", "5744");
            AddMapping("NIFTYBEES", "5893");
            AddMapping("JUNIORBEES", "4565");
            AddMapping("BANKBEES", "189");
            AddMapping("ITBEES", "4244");
            AddMapping("NIFTY", "9");
            AddMapping("NIFTY 50", "9");
            AddMapping("BANKNIFTY", "13");
            AddMapping("SENSEX", "10");
        }

        private static void AddMapping(string symbol, string token)
        {
            var cleanSymbol = symbol.ToUpperInvariant().Trim();
            if (!_tokenMap.ContainsKey(cleanSymbol))
            {
                _tokenMap[cleanSymbol] = token;
            }
        }

        public static string? GetAngelOneToken(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return null;

            var cleanSymbol = symbol
                .Replace(".NS", "")
                .Replace(".NSE", "")
                .Replace(".BO", "")
                .Replace(".BSE", "")
                .Replace("-EQ", "")
                .Trim()
                .ToUpperInvariant();

            if (_tokenMap.TryGetValue(cleanSymbol, out var token))
            {
                return token == "0" ? null : token;
            }

            var withoutSuffix = cleanSymbol
                .Replace("NSE:", "")
                .Replace("BSE:", "")
                .Replace("&", "AND")
                .Replace("-", "")
                .Trim();

            if (_tokenMap.TryGetValue(withoutSuffix, out token))
            {
                return token == "0" ? null : token;
            }

            foreach (var kvp in _tokenMap)
            {
                if (kvp.Value == "0") continue;
                if (kvp.Key.Contains(cleanSymbol) || cleanSymbol.Contains(kvp.Key))
                    return kvp.Value;
            }

            return null;
        }
    }
}