using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region Core Stock Models

    /// <summary>
    /// Represents real-time stock data from various sources
    /// </summary>
    public class StockData
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        [Required]
        public string Name { get; set; } = string.Empty;

        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }

        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }

        [Range(0, double.MaxValue)]
        public decimal DayHigh { get; set; }

        [Range(0, double.MaxValue)]
        public decimal DayLow { get; set; }

        [Range(0, long.MaxValue)]
        public long Volume { get; set; }

        public DateTime Timestamp { get; set; }

        [StringLength(12)]
        public string? Isin { get; set; }

        public string Exchange { get; set; } = "BSE";

        [Range(0, double.MaxValue)]
        public decimal YearHigh { get; set; }

        [Range(0, double.MaxValue)]
        public decimal YearLow { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Open { get; set; }

        [Range(0, double.MaxValue)]
        public decimal PreviousClose { get; set; }

        [Range(0, 1000)]
        public decimal? PE { get; set; }

        [Range(0, double.MaxValue)]
        public decimal MarketCap { get; set; }

        [Range(0, 1000)]
        public decimal FaceValue { get; set; }

        public string Sector { get; set; } = string.Empty;
        public string Industry { get; set; } = string.Empty;
    }

    /// <summary>
    /// Basic stock information from master list
    /// </summary>
    public class StockInfo
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;
        public string? Isin { get; set; }
        public string? Sector { get; set; }
        public string? Industry { get; set; }
        public string? Series { get; set; }
        public bool IsFNOSec { get; set; }
        public string? Exchange { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? MarketCap { get; set; }
    }

    #endregion

    #region API Response Models

    /// <summary>
    /// Alpha Vantage API response model
    /// </summary>
    public class AlphaVantageResponse
    {
        [JsonPropertyName("Global Quote")]
        public Dictionary<string, GlobalQuote>? GlobalQuote { get; set; }
    }

    /// <summary>
    /// Alpha Vantage quote model with JSON property mapping
    /// </summary>
    public class GlobalQuote
    {
        [JsonPropertyName("01. symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("02. open")]
        public decimal Open { get; set; }

        [JsonPropertyName("03. high")]
        public decimal High { get; set; }

        [JsonPropertyName("04. low")]
        public decimal Low { get; set; }

        [JsonPropertyName("05. price")]
        public decimal Price { get; set; }

        [JsonPropertyName("06. volume")]
        public long Volume { get; set; }

        [JsonPropertyName("07. latest trading day")]
        public DateTime LatestTradingDay { get; set; }

        [JsonPropertyName("08. previous close")]
        public decimal PreviousClose { get; set; }

        [JsonPropertyName("09. change")]
        public decimal Change { get; set; }

        [JsonPropertyName("10. change percent")]
        public string ChangePercent { get; set; } = string.Empty;
    }

    /// <summary>
    /// F&O securities response from NSE
    /// </summary>
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

    /// <summary>
    /// NSE master data response (indices)
    /// </summary>
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

    /// <summary>
    /// NSE master item (individual security)
    /// </summary>
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

    /// <summary>
    /// API rate limit tracking information
    /// </summary>
    public class RateLimitInfo
    {
        public string ApiName { get; set; } = string.Empty;
        public DateTime CooldownUntil { get; set; }
        public DateTime LastFailureTime { get; set; }
        public int FailureCount { get; set; }
    }

    #endregion

    #region NSE Response Models

    /// <summary>
    /// NSE quote API response
    /// </summary>
    public class NSEQuoteResponse
    {
        [JsonPropertyName("info")]
        public NSEInfo? Info { get; set; }

        [JsonPropertyName("metadata")]
        public NSEMetadata? Metadata { get; set; }

        [JsonPropertyName("securityInfo")]
        public NSESecurityInfo? SecurityInfo { get; set; }

        [JsonPropertyName("priceInfo")]
        public NSEPriceInfo? PriceInfo { get; set; }

        [JsonPropertyName("industryInfo")]
        public NSEIndustryInfo? IndustryInfo { get; set; }
    }

    /// <summary>
    /// NSE security information
    /// </summary>
    public class NSESecurityInfo
    {
        [JsonPropertyName("issuedSize")]
        public long? IssuedSize { get; set; }

        [JsonPropertyName("faceValue")]
        public decimal? FaceValue { get; set; }
    }

    /// <summary>
    /// NSE stock information
    /// </summary>
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

    /// <summary>
    /// NSE metadata
    /// </summary>
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

    /// <summary>
    /// NSE price information
    /// </summary>
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

    /// <summary>
    /// NSE intraday high/low
    /// </summary>
    public class NSEIntraDayHighLow
    {
        [JsonPropertyName("min")]
        public decimal Min { get; set; }

        [JsonPropertyName("max")]
        public decimal Max { get; set; }

        [JsonPropertyName("value")]
        public decimal Value { get; set; }
    }

    /// <summary>
    /// NSE 52-week high/low
    /// </summary>
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

    /// <summary>
    /// NSE industry information
    /// </summary>
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

    /// <summary>
    /// BSE quote API response
    /// </summary>
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

    /// <summary>
    /// Yahoo Finance chart response
    /// </summary>
    public class YahooFinanceResponse
    {
        [JsonPropertyName("chart")]
        public YahooChart? Chart { get; set; }
    }

    /// <summary>
    /// Yahoo Finance chart container
    /// </summary>
    public class YahooChart
    {
        [JsonPropertyName("result")]
        public List<YahooResult>? Result { get; set; }
    }

    /// <summary>
    /// Yahoo Finance result container
    /// </summary>
    public class YahooResult
    {
        [JsonPropertyName("meta")]
        public YahooMeta? Meta { get; set; }
    }

    /// <summary>
    /// Yahoo Finance metadata
    /// </summary>
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

    #region Telegram Models

    /// <summary>
    /// Telegram user subscription data
    /// </summary>
    public class TelegramUser
    {
        [Required]
        [Range(1, long.MaxValue)]
        public long ChatId { get; set; }

        public string Username { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }

        public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;

        public List<string> PreferredStocks { get; set; } = new();
        public NotificationPreferences Preferences { get; set; } = new();
    }

    /// <summary>
    /// User notification preferences
    /// </summary>
    public class NotificationPreferences
    {
        public bool DailyReport { get; set; } = true;
        public bool PriceAlerts { get; set; } = true;
        public bool MarketOpenAlert { get; set; } = false;
        public bool TopGainersLosers { get; set; } = true;
    }

    #endregion

    #region Stock News Models

    /// <summary>
    /// Stock-related news article with enhanced properties for better display
    /// </summary>
    public class StockNews
    {
        /// <summary>
        /// Primary stock symbol associated with the news (can be "Market" for general news)
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Full news headline - NEVER truncated
        /// </summary>
        [Required]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Brief summary or description of the news
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// News source (e.g., NDTV, Business Standard, Economic Times)
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// When the news was published
        /// </summary>
        public DateTime PublishedAt { get; set; }

        /// <summary>
        /// URL to the full article
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Sentiment: Positive, Negative, Neutral
        /// </summary>
        public string Sentiment { get; set; } = string.Empty;

        /// <summary>
        /// Numeric sentiment score between -1 and 1
        /// </summary>
        [Range(-1, 1)]
        public decimal? SentimentScore { get; set; }

        /// <summary>
        /// List of stock symbols mentioned in or affected by this news
        /// </summary>
        public List<string> AffectedStocks { get; set; } = new();

        /// <summary>
        /// Impact level: "🔴 HIGH IMPACT", "🟢 POSITIVE", "🟡 MEDIUM IMPACT", "⚪ GENERAL"
        /// </summary>
        public string Impact { get; set; } = string.Empty;

        /// <summary>
        /// Economic sectors affected by this news
        /// </summary>
        public List<string> AffectedSectors { get; set; } = new();

        /// <summary>
        /// Whether this is breaking news
        /// </summary>
        public bool IsBreaking { get; set; }

        /// <summary>
        /// News category: Market, Company, Sector, Economy, Global
        /// </summary>
        public string Category { get; set; } = string.Empty;
    }

    /// <summary>
    /// Daily market briefing with predictions and news
    /// </summary>
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

    /// <summary>
    /// Market indices data
    /// </summary>
    public class MarketIndices
    {
        public IndexData Nifty50 { get; set; } = new();
        public IndexData Sensex { get; set; } = new();
        public IndexData BankNifty { get; set; } = new();
        public IndexData MidCap { get; set; } = new();
        public IndexData SmallCap { get; set; } = new();
    }

    /// <summary>
    /// Individual index data
    /// </summary>
    public class IndexData
    {
        public string Name { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public decimal Change { get; set; }
        public decimal ChangePercent { get; set; }
        public decimal PreviousClose { get; set; } // Add this property
    }

    #endregion

    #region Yahoo News Models

    /// <summary>
    /// Yahoo Finance news response
    /// </summary>
    public class YahooNewsResponse
    {
        public List<YahooNewsItem>? news { get; set; }
    }

    /// <summary>
    /// Yahoo Finance news item
    /// </summary>
    public class YahooNewsItem
    {
        public string? title { get; set; }
        public string? summary { get; set; }
        public string? publisher { get; set; }
        public string? link { get; set; }

        [JsonPropertyName("providerPublishTime")]
        public long providerPublishTime { get; set; }
    }

    #endregion

    #region Portfolio Models

    /// <summary>
    /// User portfolio data
    /// </summary>
    public class UserPortfolio
    {
        [Required]
        [Range(1, long.MaxValue)]
        public long ChatId { get; set; }

        public string Username { get; set; } = string.Empty;
        public List<PortfolioHolding> Holdings { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Individual stock holding in portfolio
    /// </summary>
    public class PortfolioHolding
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;

        [Range(1, int.MaxValue)]
        public int Quantity { get; set; }

        [Range(0.01, double.MaxValue)]
        public decimal BuyPrice { get; set; }

        public DateTime BuyDate { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Portfolio performance metrics
    /// </summary>
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

    /// <summary>
    /// Individual holding performance
    /// </summary>
    public class HoldingPerformance
    {
        public string Symbol { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;

        [Range(1, int.MaxValue)]
        public int Quantity { get; set; }

        [Range(0.01, double.MaxValue)]
        public decimal BuyPrice { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CurrentPrice { get; set; }

        public decimal CurrentValue => Quantity * CurrentPrice;
        public decimal Investment => Quantity * BuyPrice;
        public decimal UnrealizedPL => CurrentValue - Investment;

        [Range(0, 1000)]
        public decimal UnrealizedPLPercent => Investment > 0 ? (UnrealizedPL / Investment) * 100 : 0;

        public decimal DayChange { get; set; }
        public decimal DayChangePercent { get; set; }
        public string Sector { get; set; } = string.Empty;
    }

    /// <summary>
    /// Portfolio summary with current values
    /// </summary>
    public class PortfolioSummary
    {
        public decimal TotalInvestment { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal TotalProfitLoss => CurrentValue - TotalInvestment;
        public decimal TotalProfitLossPercent => TotalInvestment > 0 ? (TotalProfitLoss / TotalInvestment) * 100 : 0;
        public List<Holding> Holdings { get; set; } = new();
        public Dictionary<string, decimal> SectorAllocation { get; set; } = new();
        public DateTime AsOfDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Optimization plan for portfolio
    /// </summary>
    public class OptimizationPlan
    {
        public List<TradeAction> Actions { get; set; } = new();
        public decimal TotalImpact { get; set; }
        public decimal ExpectedProfit { get; set; }
        public int TradeCount => Actions.Count;
    }

    /// <summary>
    /// Individual trade action
    /// </summary>
    public class TradeAction
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // BUY, SELL, HOLD
        public int Quantity { get; set; }
        public decimal Price { get; set; } // Entry/Exit price
        public string Reason { get; set; } = string.Empty;
        public int Priority { get; set; } // 1 = highest priority
        public decimal ExpectedProfit { get; set; }
        public decimal RiskAmount { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TargetPrice { get; set; }
        public DateTime SuggestedTime { get; set; } = DateTime.Now;
        public string Category { get; set; } = string.Empty; // Large Cap, Mid Cap, etc.
        public decimal Confidence { get; set; }
    }

    /// <summary>
    /// Sell signal for holdings
    /// </summary>
    public class SellSignal
    {
        public bool ShouldSell { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Priority { get; set; }
        public decimal SuggestedPrice { get; set; }
    }

    #endregion

    #region Enhanced Prediction Models

    /// <summary>
    /// Enhanced market analysis with historical and future predictions
    /// </summary>
    public class EnhancedMarketAnalysis
    {
        public DateTime AnalysisDate { get; set; }
        public string MarketPhase { get; set; } = string.Empty;
        public string OverallSentiment { get; set; } = string.Empty;

        [Range(0, 100)]
        public decimal ConfidenceScore { get; set; }

        public HistoricalAnalysis Past10Days { get; set; } = new();
        public FuturePredictions Next10Days { get; set; } = new();
        public CategoryAnalysis LargeCap { get; set; } = new();
        public CategoryAnalysis MidCap { get; set; } = new();
        public CategoryAnalysis SmallCap { get; set; } = new();
        public List<NewsImpactAnalysis> KeyNewsImpacts { get; set; } = new();
        public TechnicalSummary TechnicalIndicators { get; set; } = new();
    }

    /// <summary>
    /// Historical market analysis
    /// </summary>
    public class HistoricalAnalysis
    {
        public List<DailyPerformance> DailyPerformances { get; set; } = new();
        public decimal AverageChange { get; set; }
        public decimal Volatility { get; set; }
        public string Trend { get; set; } = string.Empty;
        public List<string> KeyEvents { get; set; } = new();
        public Dictionary<string, decimal> SectorPerformance { get; set; } = new();
    }

    /// <summary>
    /// Future market predictions
    /// </summary>
    public class FuturePredictions
    {
        public List<DailyPrediction> DailyPredictions { get; set; } = new();
        public string OverallOutlook { get; set; } = string.Empty;
        public decimal ExpectedReturn { get; set; }
        public string RiskLevel { get; set; } = string.Empty;
        public List<string> KeyLevels { get; set; } = new();
        public List<OpportunityAlert> Opportunities { get; set; } = new();
    }

    /// <summary>
    /// Daily market performance
    /// </summary>
    public class DailyPerformance
    {
        public DateTime Date { get; set; }
        public decimal NiftyChange { get; set; }
        public decimal SensexChange { get; set; }
        public string MarketSentiment { get; set; } = string.Empty;
        public string KeyDriver { get; set; } = string.Empty;
    }

    /// <summary>
    /// Daily market prediction
    /// </summary>
    public class DailyPrediction
    {
        public DateTime Date { get; set; }

        /// <summary>
        /// Up, Down, Sideways
        /// </summary>
        public string PredictedTrend { get; set; } = string.Empty;

        public decimal PredictedRange { get; set; }
        public string KeyFactors { get; set; } = string.Empty;

        [Range(0, 100)]
        public decimal Confidence { get; set; }
    }

    /// <summary>
    /// Category-wise analysis
    /// </summary>
    public class CategoryAnalysis
    {
        public string Category { get; set; } = string.Empty;
        public List<TopStockPick> TopPicks { get; set; } = new();
        public List<TopStockPick> AvoidStocks { get; set; } = new();
        public decimal CategoryMomentum { get; set; }
        public string Outlook { get; set; } = string.Empty;
        public Dictionary<string, decimal> TechnicalScore { get; set; } = new();
    }

    /// <summary>
    /// Top stock pick with entry/exit levels
    /// </summary>
    public class TopStockPick
    {
        [Required]
        public string Symbol { get; set; } = string.Empty;

        public string CompanyName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;

        [Range(0.01, double.MaxValue)]
        public decimal TargetPrice { get; set; }

        [Range(0.01, double.MaxValue)]
        public decimal StopLoss { get; set; }

        public string Timeframe { get; set; } = string.Empty;

        [Range(0, 100)]
        public decimal Confidence { get; set; }

        public List<string> TechnicalSignals { get; set; } = new();
        public List<string> NewsSentiment { get; set; } = new();
    }

    /// <summary>
    /// News impact analysis for enhanced market reports
    /// </summary>
    public class NewsImpactAnalysis
    {
        /// <summary>
        /// Full news headline - no truncation
        /// </summary>
        public string Headline { get; set; } = string.Empty;

        /// <summary>
        /// News source (NDTV, Business Standard, etc.)
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Publication timestamp
        /// </summary>
        public DateTime PublishedAt { get; set; }

        /// <summary>
        /// Impact level with emoji: "🔴 HIGH IMPACT", "🟢 POSITIVE", "🟡 MEDIUM IMPACT", "⚪ GENERAL"
        /// </summary>
        public string Impact { get; set; } = string.Empty;

        /// <summary>
        /// List of stock symbols affected by this news
        /// </summary>
        public List<string> AffectedStocks { get; set; } = new();

        /// <summary>
        /// List of sectors affected by this news
        /// </summary>
        public List<string> AffectedSectors { get; set; } = new();

        /// <summary>
        /// Brief summary of the news
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// URL to the full article
        /// </summary>
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// Technical indicator summary
    /// </summary>
    public class TechnicalSummary
    {
        public string RSIStatus { get; set; } = string.Empty;
        public string MACDSignal { get; set; } = string.Empty;
        public string BollingerPosition { get; set; } = string.Empty;
        public Dictionary<string, string> MovingAverages { get; set; } = new();
        public string VolumeAnalysis { get; set; } = string.Empty;
    }

    /// <summary>
    /// Trading opportunity alert
    /// </summary>
    public class OpportunityAlert
    {
        /// <summary>
        /// Breakout, Reversal, Momentum
        /// </summary>
        public string Type { get; set; } = string.Empty;

        public string Symbol { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        [Range(0.01, double.MaxValue)]
        public decimal EntryPrice { get; set; }

        [Range(0.01, double.MaxValue)]
        public decimal Target { get; set; }

        [Range(0.01, double.MaxValue)]
        public decimal StopLoss { get; set; }

        public string RiskReward { get; set; } = string.Empty;
    }

    #endregion

    #region Trading Dashboard Models

    /// <summary>
    /// Trading dashboard with today's trading signals
    /// </summary>
    public class TradingDashboard
    {
        public string MarketPhase { get; set; } = string.Empty;
        public decimal MarketConfidence { get; set; }
        public List<TradeItem> Trades { get; set; } = new();
        public TradingSummary Summary { get; set; } = new();
    }

    /// <summary>
    /// Individual trade recommendation (used in dashboard)
    /// </summary>
    public class TradeItem
    {
        public string Symbol { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public decimal TargetPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Confidence { get; set; }
        public decimal RiskReward { get; set; }
        public decimal PotentialReturn { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Trading summary statistics
    /// </summary>
    public class TradingSummary
    {
        public int TotalTrades { get; set; }
        public int HighConfidenceTrades { get; set; }
        public int MediumConfidenceTrades { get; set; }
        public decimal AverageConfidence { get; set; }
        public decimal BestRiskReward { get; set; }
        public decimal TotalPotentialReturn { get; set; }
    }

    /// <summary>
    /// Top trades response wrapper
    /// </summary>
    public class TopTradesResponse
    {
        public List<TopTradeItem> Trades { get; set; } = new();
    }

    /// <summary>
    /// Top trade item (camelCase for JSON serialization)
    /// </summary>
    public class TopTradeItem
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = string.Empty;

        [JsonPropertyName("currentPrice")]
        public decimal CurrentPrice { get; set; }

        [JsonPropertyName("targetPrice")]
        public decimal TargetPrice { get; set; }

        [JsonPropertyName("stopLoss")]
        public decimal StopLoss { get; set; }

        [JsonPropertyName("confidence")]
        public decimal Confidence { get; set; }

        [JsonPropertyName("riskReward")]
        public decimal RiskReward { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    #endregion

    #region Angel One Models

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

    public class AngelOneHoldingsResponse
    {
        public bool status { get; set; }
        public string message { get; set; }
        public string errorcode { get; set; }
        public List<AngelOneHolding> data { get; set; }
    }

    public class AngelOneHolding
    {
        public string tradingsymbol { get; set; }
        public string exchange { get; set; }
        public string isin { get; set; }
        public int t1quantity { get; set; }
        public int realisedquantity { get; set; }
        public int quantity { get; set; }
        public int authorisedquantity { get; set; }
        public string product { get; set; }
        public object collateralquantity { get; set; }
        public object collateraltype { get; set; }
        public decimal haircut { get; set; }
        public decimal averageprice { get; set; }
        public decimal ltp { get; set; }
        public string symboltoken { get; set; }
        public decimal close { get; set; }
        // Keep both for compatibility
        public decimal profitandloss { get; set; }
        public decimal? profitloss { get; set; }

        // Add a helper property
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

    /// <summary>
    /// Order request
    /// </summary>
    public class OrderRequest
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // "BUY" or "SELL"
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Exchange { get; set; } = "NSE";
        public string Variety { get; set; } = "NORMAL";
        public string OrderType { get; set; } = "LIMIT";
        public string ProductType { get; set; } = "DELIVERY";
        public string Duration { get; set; } = "DAY";
    }

    /// <summary>
    /// Order response
    /// </summary>
    public class OrderResponse
    {
        public string OrderId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime OrderTime { get; set; }
        public decimal ExecutedPrice { get; set; }
        public int ExecutedQuantity { get; set; }
    }

    /// <summary>
    /// Holding information
    /// </summary>
    public class Holding
    {
        public string Symbol { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal AveragePrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal ProfitLoss { get; set; }
        public decimal ProfitLossPercent => AveragePrice > 0 ? ((CurrentPrice - AveragePrice) / AveragePrice) * 100 : 0;
    }

    /// <summary>
    /// Position information
    /// </summary>
    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal PnL => (CurrentPrice - BuyPrice) * Quantity;
        public decimal PnLPercent => BuyPrice > 0 ? ((CurrentPrice - BuyPrice) / BuyPrice) * 100 : 0;
    }

    #endregion

    #region Groww Models

    /// <summary>
    /// Groww session information
    /// </summary>
    public class GrowwSession
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime Expiry { get; set; }
    }

    /// <summary>
    /// Groww authentication response
    /// </summary>
    public class GrowwAuthResponse
    {
        public string token { get; set; } = string.Empty;
        public DateTime expiry { get; set; }
        public string userId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Groww order response
    /// </summary>
    public class GrowwOrderResponse
    {
        public bool success { get; set; }
        public GrowwOrderData data { get; set; } = new();
        public string message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Groww order data
    /// </summary>
    public class GrowwOrderData
    {
        [JsonPropertyName("order_id")]
        public string order_id { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Groww quote
    /// </summary>
    public class GrowwQuote
    {
        public decimal ltp { get; set; }
        public decimal open { get; set; }
        public decimal high { get; set; }
        public decimal low { get; set; }
        public decimal close { get; set; }
        public long volume { get; set; }
        public decimal change { get; set; }
        public decimal changePercent { get; set; }
    }

    /// <summary>
    /// Groww positions response
    /// </summary>
    public class GrowwPositionsResponse
    {
        public bool success { get; set; }
        public List<GrowwPosition> data { get; set; } = new();
    }

    /// <summary>
    /// Groww position
    /// </summary>
    public class GrowwPosition
    {
        public string symbol { get; set; } = string.Empty;
        public int quantity { get; set; }
        public decimal average_price { get; set; }
        public decimal ltp { get; set; }
        public decimal pnl { get; set; }
        public string exchange { get; set; } = string.Empty;
    }

    #endregion

    #region Trading Execution Models

    /// <summary>
    /// Execution result for trades
    /// </summary>
    public class ExecutionResult
    {
        public bool Success { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal ExecutedPrice { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime ExecutionTime { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Daily profit and loss
    /// </summary>
    public class DailyPnL
    {
        public DateTime Date { get; set; }
        public decimal RealizedPnL { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
        public List<TradeExecution> Trades { get; set; } = new();
    }

    /// <summary>
    /// Trade execution details
    /// </summary>
    public class TradeExecution
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public DateTime Time { get; set; }
        public decimal ProfitLoss { get; set; }
        public string OrderId { get; set; } = string.Empty;
    }
    public class AngelAuthResponse
    {
        public bool status { get; set; }
        public string message { get; set; }
        public AuthData data { get; set; }
    }

    public class AuthData
    {
        public string jwtToken { get; set; }
        public string refreshToken { get; set; }
        public string feedToken { get; set; }
    }

    public class AngelHoldingResponse
    {
        public bool status { get; set; }

        public string message { get; set; }

        public List<HoldingData> data { get; set; }
    }

    public class HoldingData
    {
        public string tradingsymbol { get; set; }

        public int quantity { get; set; }

        public int t1quantity { get; set; }

        public decimal averageprice { get; set; }

        public decimal ltp { get; set; }

        public decimal profitloss { get; set; }
    }
    //public class Holding
    //{
    //    public string Symbol { get; set; }

    //    public int Quantity { get; set; }

    //    public decimal AveragePrice { get; set; }

    //    public decimal CurrentPrice { get; set; }

    //    public decimal ProfitLoss { get; set; }
    //}
    #endregion
}