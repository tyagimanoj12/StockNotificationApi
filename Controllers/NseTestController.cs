// Controllers/NseTestController.cs
using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

[ApiController]
[Route("api/[controller]")]
public class NseTestController : ControllerBase
{
    private readonly INseApiService _nseService;
    private readonly ILogger<NseTestController> _logger;

    public NseTestController(INseApiService nseService, ILogger<NseTestController> logger)
    {
        _nseService = nseService;
        _logger = logger;
    }

    [HttpGet("test")]
    public async Task<IActionResult> TestConnection()
    {
        try
        {
            var isConnected = await _nseService.TestConnectionAsync();

            return Ok(new
            {
                Success = isConnected,
                Message = isConnected ? "Connected to NSE successfully" : "Failed to connect to NSE",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NSE test failed");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("quote/{symbol}")]
    public async Task<IActionResult> GetQuote(string symbol)
    {
        try
        {
            var quote = await _nseService.GetQuoteWithRetryAsync(symbol.ToUpper());

            if (quote == null)
                return NotFound($"No data found for {symbol}");

            return Ok(new
            {
                Symbol = symbol.ToUpper(),
                Price = quote.PriceInfo?.LastPrice,
                Change = quote.PriceInfo?.Change,
                ChangePercent = quote.PriceInfo?.PChange,
                DayHigh = quote.PriceInfo?.IntraDayHighLow?.Max,
                DayLow = quote.PriceInfo?.IntraDayHighLow?.Min,
                Volume = quote.PriceInfo?.IntraDayHighLow?.Value,
                Open = quote.PriceInfo?.Open,
                PreviousClose = quote.PriceInfo?.PreviousClose,
                YearHigh = quote.PriceInfo?.WeekHighLow?.Max,
                YearLow = quote.PriceInfo?.WeekHighLow?.Min,
                CompanyName = quote.Info?.CompanyName,
                Sector = quote.IndustryInfo?.Sector,
                Industry = quote.IndustryInfo?.Industry
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quote for {Symbol}", symbol);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("symbols")]
    public async Task<IActionResult> GetSymbols([FromQuery] int? count = null)
    {
        try
        {
            var symbols = await _nseService.GetSymbolsAsync();

            if (count.HasValue && count.Value > 0)
                symbols = symbols.Take(count.Value).ToList();

            return Ok(new
            {
                Total = symbols.Count,
                Symbols = symbols
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching symbols");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}