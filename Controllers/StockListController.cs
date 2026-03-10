using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StockListController : ControllerBase
    {
        private readonly IStockListService _stockListService;
        private readonly ILogger<StockListController> _logger;

        public StockListController(
            IStockListService stockListService,
            ILogger<StockListController> logger)
        {
            _stockListService = stockListService ?? throw new ArgumentNullException(nameof(stockListService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets top NSE stocks by market cap
        /// </summary>
        /// <param name="count">Number of stocks to return (default: 50)</param>
        /// <returns>List of stocks with market cap data</returns>
        [HttpGet("nse")]
        [ProducesResponseType(typeof(List<StockInfo>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetNSEStocks([FromQuery] int count = 50)
        {
            try
            {
                if (count <= 0 || count > 200)
                {
                    return BadRequest(new { error = "Count must be between 1 and 200" });
                }

                _logger.LogInformation("Fetching top {Count} NSE stocks by market cap", count);
                var stocks = await _stockListService.GetTopStocksByMarketCapAsync(count);

                return Ok(stocks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching NSE stocks");
                return StatusCode(500, new { error = "An error occurred while fetching stocks" });
            }
        }

        /// <summary>
        /// Gets BSE stocks (limited implementation)
        /// </summary>
        /// <param name="count">Number of stocks to return (default: 50)</param>
        /// <returns>List of BSE stocks</returns>
        [HttpGet("bse")]
        [ProducesResponseType(typeof(List<StockInfo>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetBSEStocks([FromQuery] int count = 50)
        {
            try
            {
                if (count <= 0 || count > 200)
                {
                    return BadRequest(new { error = "Count must be between 1 and 200" });
                }

                _logger.LogInformation("Fetching BSE stocks");
                var stocks = await _stockListService.GetBSEStocksAsync();
                var result = stocks.Take(count).ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching BSE stocks");
                return StatusCode(500, new { error = "An error occurred while fetching BSE stocks" });
            }
        }

        /// <summary>
        /// Gets stocks by sector
        /// </summary>
        /// <param name="sector">Sector name (e.g., Banking, IT, Pharma)</param>
        /// <returns>List of stocks in the specified sector</returns>
        [HttpGet("sector/{sector}")]
        [ProducesResponseType(typeof(List<StockInfo>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetStocksBySector(string sector)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sector))
                {
                    return BadRequest(new { error = "Sector cannot be empty" });
                }

                _logger.LogInformation("Fetching stocks for sector: {Sector}", sector);
                var stocks = await _stockListService.GetStocksBySectorAsync(sector);

                if (stocks == null || !stocks.Any())
                {
                    return NotFound(new { error = $"No stocks found for sector: {sector}" });
                }

                return Ok(stocks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching stocks for sector {Sector}", sector);
                return StatusCode(500, new { error = "An error occurred while fetching stocks by sector" });
            }
        }

        /// <summary>
        /// Manually refreshes the stock list from NSE
        /// </summary>
        /// <returns>Success message</returns>
        [HttpPost("refresh")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RefreshStocks()
        {
            try
            {
                _logger.LogInformation("Manually refreshing stock list");
                await _stockListService.RefreshStockListAsync();

                return Ok(new
                {
                    message = "Stock list refreshed successfully",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing stock list");
                return StatusCode(500, new { error = "Failed to refresh stock list" });
            }
        }

        /// <summary>
        /// Debug endpoint to check market cap distribution
        /// </summary>
        /// <returns>Market cap distribution by category</returns>
        [HttpGet("debug-market-cap")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DebugMarketCap()
        {
            try
            {
                _logger.LogInformation("Fetching market cap distribution for debugging");

                var largeCap = await _stockListService.GetLargeCapStocksAsync(10);
                var midCap = await _stockListService.GetMidCapStocksAsync(10);
                var smallCap = await _stockListService.GetSmallCapStocksAsync(10);

                var result = new
                {
                    timestamp = DateTime.UtcNow,
                    largeCap = new
                    {
                        count = largeCap.Count,
                        total = largeCap.Count,
                        stocks = largeCap.Select(s => new
                        {
                            symbol = s.Symbol,
                            marketCap = s.MarketCap,
                            display = $"₹{s.MarketCap:N0} Cr"
                        })
                    },
                    midCap = new
                    {
                        count = midCap.Count,
                        total = midCap.Count,
                        stocks = midCap.Select(s => new
                        {
                            symbol = s.Symbol,
                            marketCap = s.MarketCap,
                            display = $"₹{s.MarketCap:N0} Cr"
                        })
                    },
                    smallCap = new
                    {
                        count = smallCap.Count,
                        total = smallCap.Count,
                        stocks = smallCap.Select(s => new
                        {
                            symbol = s.Symbol,
                            marketCap = s.MarketCap,
                            display = $"₹{s.MarketCap:N0} Cr"
                        })
                    }
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in debug market cap endpoint");
                return StatusCode(500, new { error = "Failed to get market cap distribution" });
            }
        }
    }
}