using System.Text.Json.Serialization;

namespace StockNotificationApi.Models
{
    #region NSE API Models

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
}
