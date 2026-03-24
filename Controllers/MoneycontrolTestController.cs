using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Controllers
{
    // Controllers/MoneycontrolTestController.cs
    [ApiController]
    [Route("api/[controller]")]
    public class MoneycontrolTestController : ControllerBase
    {
        private readonly IMoneycontrolService _moneycontrol;
        private readonly ILogger<MoneycontrolTestController> _logger;

        public MoneycontrolTestController(IMoneycontrolService moneycontrol, ILogger<MoneycontrolTestController> logger)
        {
            _moneycontrol = moneycontrol;
            _logger = logger;
        }

        [HttpGet("quote/{symbol}")]
        public async Task<IActionResult> GetQuote(string symbol)
        {
            try
            {
                var quote = await _moneycontrol.GetQuoteAsync(symbol);

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

        [HttpGet("news/{symbol}")]
        public async Task<IActionResult> GetNews(string symbol, [FromQuery] int count = 10)
        {
            try
            {
                var news = await _moneycontrol.GetStockNewsAsync(symbol, count);
                return Ok(news);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching news for {Symbol}", symbol);
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        [HttpGet("market-news")]
        public async Task<IActionResult> GetMarketNews([FromQuery] int count = 20)
        {
            try
            {
                var news = await _moneycontrol.GetMarketNewsAsync(count);
                return Ok(news);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching market news");
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        [HttpGet("indices")]
        public async Task<IActionResult> GetIndices()
        {
            try
            {
                var indices = await _moneycontrol.GetIndicesAsync();
                return Ok(indices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching indices");
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        [HttpGet("gainers")]
        public async Task<IActionResult> GetGainers()
        {
            try
            {
                var gainers = await _moneycontrol.GetTopGainersAsync();
                return Ok(gainers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching gainers");
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        [HttpGet("losers")]
        public async Task<IActionResult> GetLosers()
        {
            try
            {
                var losers = await _moneycontrol.GetTopLosersAsync();
                return Ok(losers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching losers");
                return StatusCode(500, new { Error = ex.Message });
            }
        }
    }
}