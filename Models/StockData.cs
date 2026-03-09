// Models/StockData.cs
using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    public class StockData
    {
        public string Symbol { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }
        public decimal DayHigh { get; set; }
        public decimal DayLow { get; set; }
        public long Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public string? Isin { get; set; }

        public string Exchange { get; set; } = "BSE";

        // Add missing properties
        public decimal YearHigh { get; set; }
        public decimal YearLow { get; set; }
        public decimal Open { get; set; }
        public decimal PreviousClose { get; set; }
        public decimal? PE { get; set; }
        public decimal MarketCap { get; set; }
        public decimal FaceValue { get; set; }
        public string Sector { get; set; } = string.Empty;
        public string Industry { get; set; } = string.Empty;
    }

    public class AlphaVantageResponse
    {
        public Dictionary<string, GlobalQuote> GlobalQuote { get; set; } = new();
    }

    public class GlobalQuote
    {
        [Newtonsoft.Json.JsonProperty("01. symbol")]
        public string Symbol { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonProperty("02. open")]
        public decimal Open { get; set; }

        [Newtonsoft.Json.JsonProperty("03. high")]
        public decimal High { get; set; }

        [Newtonsoft.Json.JsonProperty("04. low")]
        public decimal Low { get; set; }

        [Newtonsoft.Json.JsonProperty("05. price")]
        public decimal Price { get; set; }

        [Newtonsoft.Json.JsonProperty("06. volume")]
        public long Volume { get; set; }

        [Newtonsoft.Json.JsonProperty("07. latest trading day")]
        public DateTime LatestTradingDay { get; set; }

        [Newtonsoft.Json.JsonProperty("08. previous close")]
        public decimal PreviousClose { get; set; }

        [Newtonsoft.Json.JsonProperty("09. change")]
        public decimal Change { get; set; }

        [Newtonsoft.Json.JsonProperty("10. change percent")]
        public string ChangePercent { get; set; } = string.Empty;
    }

    public class StockInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string? Isin { get; set; }
        public string? Sector { get; set; }
        public string? Industry { get; set; }
        public string? Series { get; set; }
        public bool IsFNOSec { get; set; }
        public string? Exchange { get; set; }
        public decimal? MarketCap { get; set; }
    }

    public class FNOSecurityResponse
    {
        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("isin")]
        public string? Isin { get; set; }

        [JsonPropertyName("series")]
        public string? Series { get; set; }

        [JsonPropertyName("expiry")]
        public string? Expiry { get; set; }
    }

    public class NSEMasterResponse
    {
        [JsonPropertyName("Indices Eligible In Derivatives")]
        public List<string>? IndicesEligibleInDerivatives { get; set; }

        [JsonPropertyName("Broad Market Indices")]
        public List<string>? BroadMarketIndices { get; set; }

        [JsonPropertyName("Sectoral Market Indices")]
        public List<string>? SectoralMarketIndices { get; set; }

        [JsonPropertyName("Thematic Market Indices")]
        public List<string>? ThematicMarketIndices { get; set; }

        [JsonPropertyName("Strategy Market Indices")]
        public List<string>? StrategyMarketIndices { get; set; }

        [JsonPropertyName("Others")]
        public List<string>? Others { get; set; }
    }

    public class NSEMasterItem
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("isin")]
        public string? Isin { get; set; }

        [JsonPropertyName("sector")]
        public string? Sector { get; set; }

        [JsonPropertyName("industry")]
        public string? Industry { get; set; }

        [JsonPropertyName("series")]
        public string Series { get; set; } = string.Empty;

        [JsonPropertyName("isFNOSec")]
        public string IsFNOSec { get; set; } = "N";

        [JsonPropertyName("faceValue")]
        public decimal? FaceValue { get; set; }

        [JsonPropertyName("issuedSize")]
        public long? IssuedSize { get; set; }
    }

    #region Rate Limit Model

    public class RateLimitInfo
    {
        public string ApiName { get; set; } = string.Empty;
        public DateTime CooldownUntil { get; set; }
        public DateTime LastFailureTime { get; set; }
        public int FailureCount { get; set; }
    }

    #endregion

    #region NSE Response Models

    // In your NSEQuoteResponse class, add SecurityInfo
    public class NSEQuoteResponse
    {
        [JsonPropertyName("info")]
        public NSEInfo? Info { get; set; }

        [JsonPropertyName("metadata")]
        public NSEMetadata? Metadata { get; set; }

        [JsonPropertyName("securityInfo")]
        public NSESecurityInfo? SecurityInfo { get; set; } // ADD THIS

        [JsonPropertyName("priceInfo")]
        public NSEPriceInfo? PriceInfo { get; set; }

        [JsonPropertyName("industryInfo")]
        public NSEIndustryInfo? IndustryInfo { get; set; }
    }

    // Add this class
    public class NSESecurityInfo
    {
        [JsonPropertyName("issuedSize")]
        public long? IssuedSize { get; set; } // Total number of shares

        [JsonPropertyName("faceValue")]
        public decimal? FaceValue { get; set; }
    }

    public class NSEInfo
    {
        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("isin")]
        public string? Isin { get; set; }

        [JsonPropertyName("industry")]
        public string? Industry { get; set; }

        [JsonPropertyName("isFNOSec")]
        public bool IsFNOSec { get; set; }
    }

    public class NSEMetadata
    {
        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }

        [JsonPropertyName("isin")]
        public string? Isin { get; set; }

        [JsonPropertyName("lastUpdateTime")]
        public string? LastUpdateTime { get; set; }

        [JsonPropertyName("pdSymbolPe")]
        public decimal? PdSymbolPe { get; set; }

        [JsonPropertyName("pdSectorPe")]
        public decimal? PdSectorPe { get; set; }
    }

    public class NSEPriceInfo
    {
        [JsonPropertyName("lastPrice")]
        public decimal LastPrice { get; set; }

        [JsonPropertyName("change")]
        public decimal Change { get; set; }

        [JsonPropertyName("pChange")]
        public decimal PChange { get; set; }

        [JsonPropertyName("previousClose")]
        public decimal PreviousClose { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("close")]
        public decimal Close { get; set; }

        [JsonPropertyName("vwap")]
        public decimal Vwap { get; set; }

        [JsonPropertyName("intraDayHighLow")]
        public NSEIntraDayHighLow? IntraDayHighLow { get; set; }

        [JsonPropertyName("weekHighLow")]
        public NSEWeekHighLow? WeekHighLow { get; set; }

        [JsonPropertyName("tickSize")]
        public decimal TickSize { get; set; }
    }

    public class NSEIntraDayHighLow
    {
        [JsonPropertyName("min")]
        public decimal Min { get; set; }

        [JsonPropertyName("max")]
        public decimal Max { get; set; }

        [JsonPropertyName("value")]
        public decimal Value { get; set; }
    }

    public class NSEWeekHighLow
    {
        [JsonPropertyName("min")]
        public decimal Min { get; set; }

        [JsonPropertyName("minDate")]
        public string? MinDate { get; set; }

        [JsonPropertyName("max")]
        public decimal Max { get; set; }

        [JsonPropertyName("maxDate")]
        public string? MaxDate { get; set; }

        [JsonPropertyName("value")]
        public decimal Value { get; set; }
    }

    public class NSEIndustryInfo
    {
        [JsonPropertyName("macro")]
        public string? Macro { get; set; }

        [JsonPropertyName("sector")]
        public string? Sector { get; set; }

        [JsonPropertyName("industry")]
        public string? Industry { get; set; }

        [JsonPropertyName("basicIndustry")]
        public string? BasicIndustry { get; set; }
    }

    #endregion

    #region BSE Response Models

    public class BSEQuoteResponse
    {
        [JsonPropertyName("scrip_code")]
        public string? ScripCode { get; set; }

        [JsonPropertyName("company_name")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("current_price")]
        public decimal CurrentPrice { get; set; }

        [JsonPropertyName("change")]
        public decimal Change { get; set; }

        [JsonPropertyName("percentage_change")]
        public decimal PercentChange { get; set; }

        [JsonPropertyName("open")]
        public decimal Open { get; set; }

        [JsonPropertyName("day_high")]
        public decimal DayHigh { get; set; }

        [JsonPropertyName("day_low")]
        public decimal DayLow { get; set; }

        [JsonPropertyName("prev_close")]
        public decimal PreviousClose { get; set; }

        [JsonPropertyName("total_traded_volume")]
        public long Volume { get; set; }

        [JsonPropertyName("high_52")]
        public decimal YearHigh { get; set; }

        [JsonPropertyName("low_52")]
        public decimal YearLow { get; set; }

        [JsonPropertyName("pe")]
        public decimal? PE { get; set; }

        [JsonPropertyName("market_capitalisation")]
        public decimal MarketCap { get; set; }

        [JsonPropertyName("face_value")]
        public decimal FaceValue { get; set; }

        [JsonPropertyName("industry")]
        public string? Industry { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    #endregion

    #region Yahoo Finance Response Models

    public class YahooFinanceResponse
    {
        [JsonPropertyName("chart")]
        public YahooChart? Chart { get; set; }
    }

    public class YahooChart
    {
        [JsonPropertyName("result")]
        public List<YahooResult>? Result { get; set; }
    }

    public class YahooResult
    {
        [JsonPropertyName("meta")]
        public YahooMeta? Meta { get; set; }
    }

    public class YahooMeta
    {
        [JsonPropertyName("regularMarketPrice")]
        public decimal? RegularMarketPrice { get; set; }

        [JsonPropertyName("previousClose")]
        public decimal? PreviousClose { get; set; }

        [JsonPropertyName("regularMarketDayHigh")]
        public decimal? RegularMarketDayHigh { get; set; }

        [JsonPropertyName("regularMarketDayLow")]
        public decimal? RegularMarketDayLow { get; set; }

        [JsonPropertyName("regularMarketOpen")]
        public decimal? RegularMarketOpen { get; set; }

        [JsonPropertyName("regularMarketVolume")]
        public long? RegularMarketVolume { get; set; }
    }

    #endregion

    #region Telegram
    public class TelegramUser
    {
        public long ChatId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        public List<string> PreferredStocks { get; set; } = new(); // Empty = all stocks
        public NotificationPreferences Preferences { get; set; } = new();
    }

    public class NotificationPreferences
    {
        public bool DailyReport { get; set; } = true;
        public bool PriceAlerts { get; set; } = true;
        public bool MarketOpenAlert { get; set; } = false;
        public bool TopGainersLosers { get; set; } = true;
    }

    #endregion

    #region Stock News
    public class StockNews
    {
        public string Symbol { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Sentiment { get; set; } = string.Empty; // Positive, Negative, Neutral
        public decimal? SentimentScore { get; set; }
    }

    public class DailyBriefing
    {
        public DateTime Date { get; set; }
        public string MarketSummary { get; set; } = string.Empty;
        public List<StockPrediction> LargeCapPicks { get; set; } = new();
        public List<StockPrediction> MidCapPicks { get; set; } = new();
        public List<StockPrediction> SmallCapPicks { get; set; } = new();
        public List<StockNews> TopNews { get; set; } = new();
        public Dictionary<string, string> SectorPerformance { get; set; } = new();
        public MarketIndices Indices { get; set; } = new();
        public string TopPick { get; set; } = string.Empty;
    }

    public class MarketIndices
    {
        public IndexData Nifty50 { get; set; } = new();
        public IndexData Sensex { get; set; } = new();
        public IndexData BankNifty { get; set; } = new();
        public IndexData MidCap { get; set; } = new();
        public IndexData SmallCap { get; set; } = new();
    }

    public class IndexData
    {
        public string Name { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }
    }

    #endregion

    #region Yahoo News
    public class YahooNewsResponse
    {
        public List<YahooNewsItem>? news { get; set; }
    }

    public class YahooNewsItem
    {
        public string? title { get; set; }
        public string? summary { get; set; }
        public string? publisher { get; set; }
        public string? link { get; set; }
        public long providerPublishTime { get; set; }
    }

    #endregion

    #region Portfolio Models
    // Models/PortfolioModels.cs

    public class UserPortfolio
    {
        public long ChatId { get; set; }
        public string Username { get; set; } = string.Empty;
        public List<PortfolioHolding> Holdings { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class PortfolioHolding
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal BuyPrice { get; set; }
        public DateTime BuyDate { get; set; }
        public string? Notes { get; set; }
    }

    public class PortfolioPerformance
    {
        public decimal TotalInvestment { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal TotalPL { get; set; }
        public decimal TotalPLPercent { get; set; }
        public decimal TodayPL { get; set; }
        public List<HoldingPerformance> Holdings { get; set; } = new();
        public Dictionary<string, decimal> SectorAllocation { get; set; } = new();
    }

    public class HoldingPerformance
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal CurrentValue => Quantity * CurrentPrice;
        public decimal Investment => Quantity * BuyPrice;
        public decimal UnrealizedPL => CurrentValue - Investment;
        public decimal UnrealizedPLPercent => Investment > 0 ? (UnrealizedPL / Investment) * 100 : 0;
        public decimal DayChange { get; set; }
        public decimal DayChangePercent { get; set; }
        public string Sector { get; set; } = string.Empty;
    }
    #endregion

    #region Enhanced Prediction Model
    // Models/EnhancedPredictionModels.cs

    public class EnhancedMarketAnalysis
    {
        public DateTime AnalysisDate { get; set; }
        public string MarketPhase { get; set; } = string.Empty; // Bull, Bear, Sideways
        public string OverallSentiment { get; set; } = string.Empty;
        public decimal ConfidenceScore { get; set; }

        // Historical Analysis (Last 10 Days)
        public HistoricalAnalysis Past10Days { get; set; } = new();

        // Future Predictions (Next 10 Days)
        public FuturePredictions Next10Days { get; set; } = new();

        // Category-wise Analysis
        public CategoryAnalysis LargeCap { get; set; } = new();
        public CategoryAnalysis MidCap { get; set; } = new();
        public CategoryAnalysis SmallCap { get; set; } = new();

        // News Impact Analysis
        public List<NewsImpactAnalysis> KeyNewsImpacts { get; set; } = new();

        // Technical Indicators
        public TechnicalSummary TechnicalIndicators { get; set; } = new();
    }

    public class HistoricalAnalysis
    {
        public List<DailyPerformance> DailyPerformances { get; set; } = new();
        public decimal AverageChange { get; set; }
        public decimal Volatility { get; set; }
        public string Trend { get; set; } = string.Empty;
        public List<string> KeyEvents { get; set; } = new();
        public Dictionary<string, decimal> SectorPerformance { get; set; } = new();
    }

    public class FuturePredictions
    {
        public List<DailyPrediction> DailyPredictions { get; set; } = new();
        public string OverallOutlook { get; set; } = string.Empty;
        public decimal ExpectedReturn { get; set; }
        public string RiskLevel { get; set; } = string.Empty;
        public List<string> KeyLevels { get; set; } = new(); // Support/Resistance
        public List<OpportunityAlert> Opportunities { get; set; } = new();
    }

    public class DailyPerformance
    {
        public DateTime Date { get; set; }
        public decimal NiftyChange { get; set; }
        public decimal SensexChange { get; set; }
        public string MarketSentiment { get; set; } = string.Empty;
        public string KeyDriver { get; set; } = string.Empty;
    }

    public class DailyPrediction
    {
        public DateTime Date { get; set; }
        public string PredictedTrend { get; set; } = string.Empty; // Up, Down, Sideways
        public decimal PredictedRange { get; set; }
        public string KeyFactors { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
    }

    public class CategoryAnalysis
    {
        public string Category { get; set; } = string.Empty;
        public List<TopStockPick> TopPicks { get; set; } = new();
        public List<TopStockPick> AvoidStocks { get; set; } = new();
        public decimal CategoryMomentum { get; set; }
        public string Outlook { get; set; } = string.Empty;
        public Dictionary<string, decimal> TechnicalScore { get; set; } = new();
    }

    public class TopStockPick
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public decimal TargetPrice { get; set; }
        public decimal StopLoss { get; set; }
        public string Timeframe { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
        public List<string> TechnicalSignals { get; set; } = new();
        public List<string> NewsSentiment { get; set; } = new();
    }

    public class NewsImpactAnalysis
    {
        public string Headline { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; }
        public string AffectedSector { get; set; } = string.Empty;
        public List<string> AffectedStocks { get; set; } = new();
        public string Impact { get; set; } = string.Empty; // Positive, Negative, Neutral
        public decimal ImpactScore { get; set; }
        public string Duration { get; set; } = string.Empty; // Short-term, Long-term
    }

    //public class TechnicalSummary
    //{
    //    public string RSIStatus { get; set; } = string.Empty;
    //    public string MACDSignal { get; set; } = string.Empty;
    //    public string BollingerPosition { get; set; } = string.Empty;
    //    public Dictionary<string, string> MovingAverages { get; set; } = new();
    //    public string VolumeAnalysis { get; set; } = string.Empty;
    //}

    public class TechnicalSummary
    {
        public string RSIStatus { get; set; } = string.Empty;
        public string MACDSignal { get; set; } = string.Empty;
        public string BollingerPosition { get; set; } = string.Empty;
        public Dictionary<string, string> MovingAverages { get; set; } = new();
        public string VolumeAnalysis { get; set; } = string.Empty;
    }

    public class OpportunityAlert
    {
        public string Type { get; set; } = string.Empty; // Breakout, Reversal, Momentum
        public string Symbol { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public decimal Target { get; set; }
        public decimal StopLoss { get; set; }
        public string RiskReward { get; set; } = string.Empty;
    }
    #endregion
}