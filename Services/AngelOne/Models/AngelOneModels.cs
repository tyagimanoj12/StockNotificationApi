//using System.Text.Json.Serialization;

//namespace StockNotificationApi.Services.AngelOne.Models
//{
//    public record AngelOneCredentials(string ApiKey, string ClientId, string Mpin, string TotpSecret);

//    public class AngelOneSession
//    {
//        public string AuthToken { get; set; } = string.Empty;
//        public string RefreshToken { get; set; } = string.Empty;
//        public string FeedToken { get; set; } = string.Empty;
//        public string UserId { get; set; } = string.Empty;
//        public DateTime ExpiresAt { get; set; }
//    }

//    public class AngelOneAuthResponse
//    {
//        public bool status { get; set; }
//        public string message { get; set; } = string.Empty;
//        public string errorcode { get; set; } = string.Empty;
//        public AuthData? data { get; set; }
//    }

//    public class AuthData
//    {
//        public string jwtToken { get; set; } = string.Empty;
//        public string refreshToken { get; set; } = string.Empty;
//        public string feedToken { get; set; } = string.Empty;
//        public string userId { get; set; } = string.Empty;
//    }

//    public class AngelOneHoldingsResponse
//    {
//        public bool status { get; set; }
//        public string message { get; set; } = string.Empty;
//        public string errorcode { get; set; } = string.Empty;
//        public List<HoldingData>? data { get; set; }
//    }

//    public class HoldingData
//    {
//        public string tradingsymbol { get; set; } = string.Empty;
//        public int quantity { get; set; }
//        public int t1quantity { get; set; }
//        public decimal averageprice { get; set; }
//        public decimal ltp { get; set; }
//        public decimal? profitloss { get; set; }
//        public decimal? profitandloss { get; set; }
//    }

//    public class AngelOnePositionsResponse
//    {
//        public bool status { get; set; }
//        public string message { get; set; } = string.Empty;
//        public List<PositionData>? data { get; set; }
//    }

//    public class PositionData
//    {
//        public string tradingsymbol { get; set; } = string.Empty;
//        public int quantity { get; set; }
//        public decimal buyprice { get; set; }
//        public decimal ltp { get; set; }
//    }

//    public class AngelOneOrderResponse
//    {
//        public bool status { get; set; }
//        public string message { get; set; } = string.Empty;
//        public OrderData? data { get; set; }
//    }

//    public class OrderData
//    {
//        public string orderid { get; set; } = string.Empty;
//    }

//    public class AngelOneMarketDataResponse
//    {
//        public bool Status { get; set; }
//        public string Message { get; set; } = string.Empty;
//        public MarketData? Data { get; set; }
//    }

//    public class MarketData
//    {
//        public List<AngelOneMarketQuote> Fetched { get; set; } = new();
//        public List<UnfetchedData> Unfetched { get; set; } = new();
//    }

//    public class AngelOneMarketQuote
//    {
//        public string Exchange { get; set; } = string.Empty;
//        public string SymbolToken { get; set; } = string.Empty;
//        public string TradingSymbol { get; set; } = string.Empty;
//        public decimal Ltp { get; set; }
//        public decimal NetChange { get; set; }
//        public decimal PercentChange { get; set; }
//        public decimal High { get; set; }
//        public decimal Low { get; set; }
//        public decimal Open { get; set; }
//        public decimal Close { get; set; }
//        public long TradeVolume { get; set; }
//        public decimal YearHigh52 { get; set; }
//        public decimal YearLow52 { get; set; }
//    }

//    public class UnfetchedData
//    {
//        public string Exchange { get; set; } = string.Empty;
//        public string SymbolToken { get; set; } = string.Empty;
//        public string Message { get; set; } = string.Empty;
//    }

//    public class ScripMasterEntry
//    {
//        public string exch_seg { get; set; } = string.Empty;
//        public string symbol { get; set; } = string.Empty;
//        public string name { get; set; } = string.Empty;
//        public string token { get; set; } = string.Empty;
//        public string instrumenttype { get; set; } = string.Empty;
//        public int lotsize { get; set; }
//        public decimal strike { get; set; }
//        public string expiry { get; set; } = string.Empty;
//    }

//    public class AngelOneMasterQuote
//    {
//        public string Symbol { get; set; } = string.Empty;
//        public string TradingSymbol { get; set; } = string.Empty;
//        public string CompanyName { get; set; } = string.Empty;
//        public string Token { get; set; } = string.Empty;
//        public string Exchange { get; set; } = string.Empty;
//        public string InstrumentType { get; set; } = string.Empty;
//        public int LotSize { get; set; }
//        public decimal Strike { get; set; }
//        public string Expiry { get; set; } = string.Empty;
//    }
//}