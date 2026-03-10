namespace StockNotificationApi.Data
{
    public static class SymbolTokenMap
    {
        public static readonly Dictionary<string, string> AngelOneTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            // Nifty 50 Stocks
            ["RELIANCE"] = "2885",
            ["TCS"] = "11536",
            ["HDFCBANK"] = "1333",
            ["INFY"] = "1594",
            ["ICICIBANK"] = "4963",
            ["HINDUNILVR"] = "1394",
            ["ITC"] = "1660",
            ["SBIN"] = "3045",
            ["BHARTIARTL"] = "10604",
            ["KOTAKBANK"] = "1922",
            ["LT"] = "236",
            ["ASIANPAINT"] = "6018",
            ["MARUTI"] = "2475",
            ["TATAMOTORS"] = "3456",
            ["AXISBANK"] = "5900",
            ["HCLTECH"] = "7229",
            ["SUNPHARMA"] = "3351",
            ["TITAN"] = "3506",
            ["WIPRO"] = "3787",
            ["ULTRACEMCO"] = "3608",
            ["BAJFINANCE"] = "6839",
            ["ADANIPORTS"] = "3869",
            ["NTPC"] = "2773",
            ["ONGC"] = "2783",
            ["POWERGRID"] = "2951",
            ["M&M"] = "2431",
            ["BAJAJFINSV"] = "6867",
            ["TATASTEEL"] = "3499",
            ["JSWSTEEL"] = "8770",
            ["TECHM"] = "3525",
            ["INDUSINDBK"] = "5258",
            ["NESTLEIND"] = "2722",
            ["HDFCLIFE"] = "467187",
            ["SBILIFE"] = "470771",
            ["DIVISLAB"] = "45437",
            ["DRREDDY"] = "4165",
            ["BRITANNIA"] = "3661",
            ["GRASIM"] = "4541",
            ["EICHERMOT"] = "1389",
            ["COALINDIA"] = "4087",
            ["BPCL"] = "5168",
            ["HINDALCO"] = "1375",
            ["IOC"] = "1624",
            ["SHREECEM"] = "3064",
            ["UPL"] = "13245",
            ["HEROMOTOCO"] = "1363",
            ["BAJAJ-AUTO"] = "1390",

            // Mid Cap Stocks
            ["CAMS"] = "246265",
            ["ANGELONE"] = "86449",
            ["SAMMAANCAP"] = "219517",
            ["DMART"] = "121176",
            ["PGEL"] = "222207",
            ["TVSMOTOR"] = "3574",
            ["PERSISTENT"] = "247557",
            ["LTTS"] = "210468",
            ["FEDERALBNK"] = "1164",
            ["CRISIL"] = "2293",
            ["ALKEM"] = "65009",
            ["KAYNES"] = "247565",
            ["KPITTECH"] = "212760",
            ["MAPMYINDIA"] = "242811",
            ["PNBHOUSING"] = "209567",
            ["LAURUSLABS"] = "224779",
            ["PEL"] = "2859",

            // Bank Nifty Stocks
            ["BANKBARODA"] = "305",
            ["CANBK"] = "878",
            ["IDFCFIRSTB"] = "157574",
            ["INDIANB"] = "1532",
            ["RBLBANK"] = "130126",
            ["FEDERALBNK"] = "1164",
            ["BANDHANBNK"] = "115812"
        };

        public static readonly Dictionary<string, string> NseToBseMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RELIANCE"] = "500325",
            ["TCS"] = "532540",
            ["HDFCBANK"] = "500180",
            ["INFY"] = "500209",
            ["ICICIBANK"] = "532174",
            ["HINDUNILVR"] = "500696",
            ["ITC"] = "500875",
            ["SBIN"] = "500112",
            ["BHARTIARTL"] = "532454",
            ["KOTAKBANK"] = "500247",
            ["LT"] = "500510",
            ["ASIANPAINT"] = "500820",
            ["MARUTI"] = "532500",
            ["TATAMOTORS"] = "500570",
            ["AXISBANK"] = "532155",
            ["HCLTECH"] = "500185",
            ["SUNPHARMA"] = "524715",
            ["TITAN"] = "500114",
            ["WIPRO"] = "507685",
            ["ULTRACEMCO"] = "532538"
        };

        public static string GetAngelOneToken(string symbol)
        {
            return AngelOneTokens.TryGetValue(symbol, out var token) ? token : null;
        }

        public static string GetBSECode(string symbol)
        {
            return NseToBseMap.TryGetValue(symbol, out var code) ? code : null;
        }
    }
}