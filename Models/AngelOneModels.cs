using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    // Market Quote Response
    public class AngelOneQuoteResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public AngelOneQuoteData? Data { get; set; }
    }

    public class AngelOneQuoteData
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("companyName")]
        public string CompanyName { get; set; } = string.Empty;

        [JsonPropertyName("exchange")]
        public string Exchange { get; set; } = string.Empty;

        [JsonPropertyName("ltp")]
        public decimal Ltp { get; set; } // Last Traded Price

        [JsonPropertyName("change")]
        public decimal Change { get; set; }

        [JsonPropertyName("changePercentage")]
        public decimal ChangePercentage { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("high")]
        public decimal High { get; set; }

        [JsonPropertyName("low")]
        public decimal Low { get; set; }

        [JsonPropertyName("close")]
        public decimal Close { get; set; }

        [JsonPropertyName("volume")]
        public long Volume { get; set; }

        [JsonPropertyName("weekHigh52")]
        public decimal WeekHigh52 { get; set; }

        [JsonPropertyName("weekLow52")]
        public decimal WeekLow52 { get; set; }

        [JsonPropertyName("marketCap")]
        public decimal MarketCap { get; set; }

        [JsonPropertyName("peRatio")]
        public decimal? PeRatio { get; set; }

        [JsonPropertyName("sector")]
        public string? Sector { get; set; }

        [JsonPropertyName("industry")]
        public string? Industry { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    // Bulk Quote Response
    public class AngelOneBulkQuoteResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("data")]
        public List<AngelOneQuoteData> Data { get; set; } = new();
    }

    // Indices Response
    public class AngelOneIndicesResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("data")]
        public List<AngelOneIndexData> Data { get; set; } = new();
    }

    public class AngelOneIndexData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public decimal Value { get; set; }

        [JsonPropertyName("change")]
        public decimal Change { get; set; }

        [JsonPropertyName("changePercentage")]
        public decimal ChangePercentage { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("high")]
        public decimal High { get; set; }

        [JsonPropertyName("low")]
        public decimal Low { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    // Historical Data Response
    public class AngelOneHistoricalResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("data")]
        public List<AngelOneCandle> Data { get; set; } = new();
    }

    public class AngelOneCandle
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("high")]
        public decimal High { get; set; }

        [JsonPropertyName("low")]
        public decimal Low { get; set; }

        [JsonPropertyName("close")]
        public decimal Close { get; set; }

        [JsonPropertyName("volume")]
        public long Volume { get; set; }
    }

    // Add to Models/AngelOneMarketModels.cs
    public class AngelOneMarketDataResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("errorcode")]
        public string ErrorCode { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public AngelOneMarketData Data { get; set; } = new();
    }

    public class AngelOneMarketData
    {
        [JsonPropertyName("fetched")]
        public List<AngelOneMarketQuote> Fetched { get; set; } = new();

        [JsonPropertyName("unfetched")]
        public List<AngelOneUnfetched> Unfetched { get; set; } = new();
    }

    public class AngelOneMarketQuote
    {
        [JsonPropertyName("exchange")]
        public string Exchange { get; set; } = string.Empty;

        [JsonPropertyName("tradingSymbol")]
        public string TradingSymbol { get; set; } = string.Empty;

        [JsonPropertyName("symbolToken")]
        public string SymbolToken { get; set; } = string.Empty;

        [JsonPropertyName("ltp")]
        public decimal Ltp { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("high")]
        public decimal High { get; set; }

        [JsonPropertyName("low")]
        public decimal Low { get; set; }

        [JsonPropertyName("close")]
        public decimal Close { get; set; }

        [JsonPropertyName("netChange")]
        public decimal NetChange { get; set; }

        [JsonPropertyName("percentChange")]
        public decimal PercentChange { get; set; }

        [JsonPropertyName("tradeVolume")]
        public long TradeVolume { get; set; }

        [JsonPropertyName("52WeekHigh")]
        public decimal YearHigh52 { get; set; }

        [JsonPropertyName("52WeekLow")]
        public decimal YearLow52 { get; set; }

        [JsonPropertyName("lastTradeQty")]
        public int LastTradeQty { get; set; }

        [JsonPropertyName("exchFeedTime")]
        public string ExchFeedTime { get; set; } = string.Empty;

        [JsonPropertyName("exchTradeTime")]
        public string ExchTradeTime { get; set; } = string.Empty;

        [JsonPropertyName("avgPrice")]
        public decimal AvgPrice { get; set; }

        [JsonPropertyName("opnInterest")]
        public long OpenInterest { get; set; }

        [JsonPropertyName("upperCircuit")]
        public decimal UpperCircuit { get; set; }

        [JsonPropertyName("lowerCircuit")]
        public decimal LowerCircuit { get; set; }

        [JsonPropertyName("totBuyQuan")]
        public long TotalBuyQuantity { get; set; }

        [JsonPropertyName("totSellQuan")]
        public long TotalSellQuantity { get; set; }

        [JsonPropertyName("depth")]
        public MarketDepth Depth { get; set; } = new();
    }

    public class MarketDepth
    {
        [JsonPropertyName("buy")]
        public List<DepthLevel> Buy { get; set; } = new();

        [JsonPropertyName("sell")]
        public List<DepthLevel> Sell { get; set; } = new();
    }

    public class DepthLevel
    {
        [JsonPropertyName("price")]
        public decimal Price { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("orders")]
        public int Orders { get; set; }
    }

    public class AngelOneUnfetched
    {
        [JsonPropertyName("exchange")]
        public string Exchange { get; set; } = string.Empty;

        [JsonPropertyName("symbolToken")]
        public string SymbolToken { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; } = string.Empty;
    }

    // Add to Models/AngelOneMarketModels.cs
    public class AngelOneMasterQuoteResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public List<AngelOneMasterQuote> Data { get; set; } = new();
    }

    public class AngelOneMasterQuote
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("companyName")]
        public string CompanyName { get; set; } = string.Empty;

        [JsonPropertyName("exchange")]
        public string Exchange { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("sector")]
        public string Sector { get; set; } = string.Empty;

        [JsonPropertyName("industry")]
        public string Industry { get; set; } = string.Empty;

        [JsonPropertyName("isFNOSec")]
        public bool IsFNOSec { get; set; }
        public string TradingSymbol { get; set; }
        public string InstrumentType { get; set; }
        public string LotSize { get; set; }
        public string Strike { get; set; }
        public string Expiry { get; set; }

        // Additional properties from your existing model
        public string Name { get; set; }
        public decimal LastPrice { get; set; }
        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }
    }

    /// <summary>
    /// Model for the Angel One scrip master JSON structure
    /// </summary>
    public class ScripMasterEntry
    {
        [JsonPropertyName("token")]
        public string token { get; set; }

        [JsonPropertyName("symbol")]
        public string symbol { get; set; }

        [JsonPropertyName("name")]
        public string name { get; set; }

        [JsonPropertyName("expiry")]
        public string expiry { get; set; }

        [JsonPropertyName("strike")]
        public string strike { get; set; }

        [JsonPropertyName("lotsize")]
        public string lotsize { get; set; }

        [JsonPropertyName("instrumenttype")]
        public string instrumenttype { get; set; }

        [JsonPropertyName("exch_seg")]
        public string exch_seg { get; set; }

        [JsonPropertyName("tick_size")]
        public string tick_size { get; set; }
    }

    #region Angel One API Models

    /// <summary>
    /// Angel One session information
    /// </summary>
    public class AngelOneSession
    {
        public string AuthToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string FeedToken { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// Angel One authentication response
    /// </summary>
    public class AngelOneAuthResponse
    {
        public bool status { get; set; }
        public string message { get; set; } = string.Empty;
        public string errorCode { get; set; } = string.Empty;
        public AngelOneAuthData data { get; set; } = new();
    }

    /// <summary>
    /// Angel One authentication data
    /// </summary>
    public class AngelOneAuthData
    {
        public string jwtToken { get; set; } = string.Empty;
        public string refreshToken { get; set; } = string.Empty;
        public string feedToken { get; set; } = string.Empty;
        public string userId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Angel One order response
    /// </summary>
    public class AngelOneOrderResponse
    {
        public bool status { get; set; }
        public string message { get; set; } = string.Empty;
        public string errorCode { get; set; } = string.Empty;
        public AngelOneOrderData data { get; set; } = new();
    }

    /// <summary>
    /// Angel One order data
    /// </summary>
    public class AngelOneOrderData
    {
        public string orderid { get; set; } = string.Empty;
        public string uniqueorderid { get; set; } = string.Empty;
    }

    /// <summary>
    /// Angel One holdings response
    /// </summary>
    public class AngelOneHoldingsResponse
    {
        public bool status { get; set; }
        public string message { get; set; } = string.Empty;
        public string errorcode { get; set; } = string.Empty;
        public List<AngelOneHolding> data { get; set; } = new();
    }

    /// <summary>
    /// Angel One holding
    /// </summary>
    public class AngelOneHolding
    {
        public string tradingsymbol { get; set; } = string.Empty;
        public string exchange { get; set; } = string.Empty;
        public string isin { get; set; } = string.Empty;
        public int t1quantity { get; set; }
        public int realisedquantity { get; set; }
        public int quantity { get; set; }
        public int authorisedquantity { get; set; }
        public string product { get; set; } = string.Empty;
        public object collateralquantity { get; set; } = new();
        public object collateraltype { get; set; } = new();
        public decimal haircut { get; set; }
        public decimal averageprice { get; set; }
        public decimal ltp { get; set; }
        public string symboltoken { get; set; } = string.Empty;
        public decimal close { get; set; }
        public decimal profitandloss { get; set; }
        public decimal? profitloss { get; set; }

        [JsonIgnore]
        public decimal EffectiveProfitLoss => profitloss ?? profitandloss;

        public decimal pnlpercentage { get; set; }
    }

    /// <summary>
    /// Angel One positions response
    /// </summary>
    public class AngelOnePositionsResponse
    {
        public bool status { get; set; }
        public string message { get; set; } = string.Empty;
        public List<AngelOnePosition> data { get; set; } = new();
    }

    /// <summary>
    /// Angel One position
    /// </summary>
    public class AngelOnePosition
    {
        public string tradingsymbol { get; set; } = string.Empty;
        public string symboltoken { get; set; } = string.Empty;
        public string exchange { get; set; } = string.Empty;
        public int quantity { get; set; }
        public decimal buyprice { get; set; }
        public decimal ltp { get; set; }
        public decimal profitloss { get; set; }
    }

    #endregion
}