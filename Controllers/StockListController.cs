// Controllers/StockListController.cs
using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

[ApiController]
[Route("api/[controller]")]
public class StockListController : ControllerBase
{
    private readonly IStockListService _stockListService;

    public StockListController(IStockListService stockListService)
    {
        _stockListService = stockListService;
    }

    [HttpGet("nse")]
    public async Task<IActionResult> GetNSEStocks([FromQuery] int count = 50)
    {
        var stocks = await _stockListService.GetTopStocksByMarketCapAsync(count);
        return Ok(stocks);
    }

    [HttpGet("bse")]
    public async Task<IActionResult> GetBSEStocks([FromQuery] int count = 50)
    {
        var stocks = await _stockListService.GetBSEStocksAsync();
        return Ok(stocks.Take(count));
    }

    [HttpGet("sector/{sector}")]
    public async Task<IActionResult> GetStocksBySector(string sector)
    {
        var stocks = await _stockListService.GetStocksBySectorAsync(sector);
        return Ok(stocks);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshStocks()
    {
        await _stockListService.RefreshStockListAsync();
        return Ok(new { message = "Stock list refreshed" });
    }

    [HttpGet("debug-market-cap")]
    public async Task<IActionResult> DebugMarketCap()
    {
        var largeCap = await _stockListService.GetLargeCapStocksAsync(10);
        var midCap = await _stockListService.GetMidCapStocksAsync(10);
        var smallCap = await _stockListService.GetSmallCapStocksAsync(10);

        return Ok(new
        {
            largeCap = new
            {
                count = largeCap.Count,
                stocks = largeCap.Select(s => $"{s.Symbol}: ₹{s.MarketCap:N0} Cr")
            },
            midCap = new
            {
                count = midCap.Count,
                stocks = midCap.Select(s => $"{s.Symbol}: ₹{s.MarketCap:N0} Cr")
            },
            smallCap = new
            {
                count = smallCap.Count,
                stocks = smallCap.Select(s => $"{s.Symbol}: ₹{s.MarketCap:N0} Cr")
            }
        });
    }
}