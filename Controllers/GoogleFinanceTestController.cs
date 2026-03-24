// Controllers/GoogleFinanceTestController.cs
using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

[ApiController]
[Route("api/[controller]")]
public class GoogleFinanceTestController : ControllerBase
{
    private readonly IGoogleFinanceService _googleFinance;
    private readonly ILogger<GoogleFinanceTestController> _logger;

    public GoogleFinanceTestController(IGoogleFinanceService googleFinance, ILogger<GoogleFinanceTestController> logger)
    {
        _googleFinance = googleFinance;
        _logger = logger;
    }

    [HttpGet("quote/{symbol}")]
    public async Task<IActionResult> GetQuote(string symbol)
    {
        try
        {
            var quote = await _googleFinance.GetQuoteAsync(symbol);

            if (quote == null)
                return NotFound($"No data found for {symbol}");

            return Ok(quote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching quote for {Symbol}", symbol);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet("currency/{from}/{to}")]
    public async Task<IActionResult> GetCurrencyRate(string from, string to)
    {
        try
        {
            // You'd need to add this method to the service
            return Ok(new { Message = "Not implemented yet" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching currency rate");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}