//using System.Text.Json.Serialization;

//namespace StockNotificationApi.Models
//{
//    #region Angel One Models

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
//        public string errorCode { get; set; } = string.Empty;
//        public AngelOneAuthData data { get; set; } = new();
//    }

//    public class AngelOneAuthData
//    {
//        public string jwtToken { get; set; } = string.Empty;
//        public string refreshToken { get; set; } = string.Empty;
//        public string feedToken { get; set; } = string.Empty;
//        public string userId { get; set; } = string.Empty;
//    }

//    public class AngelOneOrderResponse
//    {
//        public bool status { get; set; }
//        public string message { get; set; } = string.Empty;
//        public string errorCode { get; set; } = string.Empty;
//        public AngelOneOrderData data { get; set; } = new();
//    }

//    public class AngelOneOrderData
//    {
//        public string orderid { get; set; } = string.Empty;
//        public string uniqueorderid { get; set; } = string.Empty;
//    }

//    public class AngelOneHoldingsResponse
//    {
//        public bool status { get; set; }
//        public string message { get; set; } = string.Empty;
//        public string errorCode { get; set; } = string.Empty;
//        public List<AngelOneHolding> data { get; set; } = new();
//    }

//    public class AngelOneHolding
//    {
//        public string tradingsymbol { get; set; } = string.Empty;
//        public string symboltoken { get; set; } = string.Empty;
//        public string exchange { get; set; } = string.Empty;
//        public int quantity { get; set; }
//        public int t1quantity { get; set; }
//        public decimal averageprice { get; set; }
//        public decimal ltp { get; set; }
//        public decimal profitloss { get; set; }
//        public decimal realisedquantity { get; set; }
//        public string producttype { get; set; } = string.Empty;
//        public string collateralkey { get; set; } = string.Empty;
//        public string holdingcost { get; set; } = string.Empty;
//    }

//    public class OrderRequest
//    {
//        public string Symbol { get; set; } = string.Empty;
//        public string Action { get; set; } = string.Empty; // "BUY" or "SELL"
//        public int Quantity { get; set; }
//        public decimal Price { get; set; }
//        public string Exchange { get; set; } = "NSE";
//        public string Variety { get; set; } = "NORMAL";
//        public string OrderType { get; set; } = "LIMIT";
//        public string ProductType { get; set; } = "DELIVERY";
//        public string Duration { get; set; } = "DAY";
//    }

//    public class OrderResponse
//    {
//        public string OrderId { get; set; } = string.Empty;
//        public string Status { get; set; } = string.Empty;
//        public string Message { get; set; } = string.Empty;
//        public DateTime OrderTime { get; set; }
//        public decimal ExecutedPrice { get; set; }
//        public int ExecutedQuantity { get; set; }
//    }

//    public class Holding
//    {
//        public string Symbol { get; set; } = string.Empty;
//        public int Quantity { get; set; }
//        public decimal AveragePrice { get; set; }
//        public decimal CurrentPrice { get; set; }
//        public decimal ProfitLoss { get; set; }
//        public decimal ProfitLossPercent => AveragePrice > 0 ? ((CurrentPrice - AveragePrice) / AveragePrice) * 100 : 0;
//    }

//    public class Position
//    {
//        public string Symbol { get; set; } = string.Empty;
//        public int Quantity { get; set; }
//        public decimal BuyPrice { get; set; }
//        public decimal CurrentPrice { get; set; }
//        public decimal PnL => (CurrentPrice - BuyPrice) * Quantity;
//        public decimal PnLPercent => BuyPrice > 0 ? ((CurrentPrice - BuyPrice) / BuyPrice) * 100 : 0;
//    }

//    #endregion

//    #region Groww Models

//    public class GrowwSession
//    {
//        public string AccessToken { get; set; } = string.Empty;
//        public DateTime Expiry { get; set; }
//    }

//    //public class GrowwAuthResponse
//    //{
//    //    public string token { get; set; } = string.Empty;
//    //    public DateTime expiry { get; set; }
//    //    public string userId { get; set; } = string.Empty;
//    //}

//    //public class GrowwOrderResponse
//    //{
//    //    public bool success { get; set; }
//    //    public GrowwOrderData data { get; set; } = new();
//    //    public string message { get; set; } = string.Empty;
//    //}

//    //public class GrowwOrderData
//    //{
//    //    [JsonPropertyName("order_id")]
//    //    public string order_id { get; set; } = string.Empty;

//    //    [JsonPropertyName("status")]
//    //    public string status { get; set; } = string.Empty;

//    //    [JsonPropertyName("message")]
//    //    public string message { get; set; } = string.Empty;
//    //}

//    //public class GrowwQuote
//    //{
//    //    public decimal ltp { get; set; }
//    //    public decimal open { get; set; }
//    //    public decimal high { get; set; }
//    //    public decimal low { get; set; }
//    //    public decimal close { get; set; }
//    //    public long volume { get; set; }
//    //    public decimal change { get; set; }
//    //    public decimal changePercent { get; set; }
//    //}

//    //public class GrowwPosition
//    //{
//    //    public string symbol { get; set; } = string.Empty;
//    //    public int quantity { get; set; }
//    //    public decimal avgPrice { get; set; }
//    //    public decimal ltp { get; set; }
//    //    public decimal pnl { get; set; }
//    //}

//    #endregion

//    #region Portfolio Summary Models

//    //public class PortfolioSummary
//    //{
//    //    public decimal TotalValue { get; set; }
//    //    public decimal TotalInvestment { get; set; }
//    //    public decimal TotalProfitLoss { get; set; }
//    //    public decimal TotalProfitLossPercent => TotalInvestment > 0 ? (TotalProfitLoss / TotalInvestment) * 100 : 0;
//    //    public List<Holding> Holdings { get; set; } = new();
//    //    public Dictionary<string, decimal> SectorAllocation { get; set; } = new();
//    //    public DateTime AsOfDate { get; set; } = DateTime.Now;
//    //}

//    //public class OptimizationPlan
//    //{
//    //    public List<TradeAction> Actions { get; set; } = new();
//    //    public decimal TotalImpact { get; set; }
//    //    public decimal ExpectedProfit { get; set; }
//    //    public int TradeCount => Actions.Count;
//    //}

//    //public class TradeAction
//    //{
//    //    public string Symbol { get; set; } = string.Empty;
//    //    public string Action { get; set; } = string.Empty; // BUY, SELL, HOLD
//    //    public int Quantity { get; set; }
//    //    public decimal EstimatedPrice { get; set; }
//    //    public string Reason { get; set; } = string.Empty;
//    //    public int Priority { get; set; } // 1 = highest
//    //    public decimal ExpectedProfit { get; set; }
//    //    public decimal RiskAmount { get; set; }
//    //}

//    public class SellSignal
//    {
//        public bool ShouldSell { get; set; }
//        public string Reason { get; set; } = string.Empty;
//        public int Priority { get; set; }
//        public decimal SuggestedPrice { get; set; }
//    }

//    #endregion

//    #region Trading Execution Models

//    public class ExecutionResult
//    {
//        public bool Success { get; set; }
//        public string OrderId { get; set; } = string.Empty;
//        public string Symbol { get; set; } = string.Empty;
//        public int Quantity { get; set; }
//        public decimal ExecutedPrice { get; set; }
//        public string Message { get; set; } = string.Empty;
//        public DateTime ExecutionTime { get; set; } = DateTime.Now;
//    }

//    public class DailyPnL
//    {
//        public DateTime Date { get; set; }
//        public decimal RealizedPnL { get; set; }
//        public decimal UnrealizedPnL { get; set; }
//        public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
//        public List<TradeExecution> Trades { get; set; } = new();
//    }

//    public class TradeExecution
//    {
//        public string Symbol { get; set; } = string.Empty;
//        public string Action { get; set; } = string.Empty;
//        public int Quantity { get; set; }
//        public decimal Price { get; set; }
//        public DateTime Time { get; set; }
//        public decimal ProfitLoss { get; set; }
//        public string OrderId { get; set; } = string.Empty;
//    }

//    #endregion
//}