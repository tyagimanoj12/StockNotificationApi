using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StockPredictionController : ControllerBase
    {
        private readonly IStockService _stockService;
        private readonly IAIService _aiService;
        private readonly INotificationService _notificationService;
        private readonly ILogger<StockPredictionController> _logger;

        public StockPredictionController(
            IStockService stockService,
            IAIService aiService,
            INotificationService notificationService,
            ILogger<StockPredictionController> logger)
        {
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets all Indian stocks with real-time data
        /// </summary>
        /// <returns>List of stocks with current prices and changes</returns>
        [HttpGet("stocks")]
        [ProducesResponseType(typeof(List<StockData>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetIndianStocks()
        {
            try
            {
                _logger.LogInformation("Fetching all Indian stocks");
                var stocks = await _stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stocks data retrieved");
                    return Ok(new List<StockData>());
                }

                return Ok(stocks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching stocks");
                return StatusCode(500, new { error = "Failed to fetch stock data", timestamp = DateTime.UtcNow });
            }
        }

        /// <summary>
        /// Gets detailed information for a specific stock
        /// </summary>
        /// <param name="symbol">Stock symbol (e.g., RELIANCE.NS, TCS.BO)</param>
        /// <returns>Detailed stock information</returns>
        [HttpGet("stock/{symbol}")]
        [ProducesResponseType(typeof(StockData), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetStock(string symbol)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    return BadRequest(new { error = "Symbol cannot be empty" });
                }

                _logger.LogInformation("Fetching stock data for {Symbol}", symbol);
                var stock = await _stockService.GetStockDataAsync(symbol.ToUpper());

                if (stock == null)
                {
                    _logger.LogWarning("Stock not found: {Symbol}", symbol);
                    return NotFound(new { error = $"Stock not found: {symbol}" });
                }

                return Ok(stock);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching stock {Symbol}", symbol);
                return StatusCode(500, new { error = "Failed to fetch stock data", symbol });
            }
        }

        /// <summary>
        /// Gets AI-generated predictions for all stocks
        /// </summary>
        /// <returns>Market summary and stock predictions</returns>
        [HttpGet("predictions")]
        [ProducesResponseType(typeof(DailyPredictionReport), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPredictions()
        {
            try
            {
                _logger.LogInformation("Generating stock predictions");

                var stocks = await _stockService.GetIndianStockDataAsync();
                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stocks data available for predictions");
                    return Ok(new DailyPredictionReport
                    {
                        Date = DateTime.Now,
                        MarketSummary = "No stock data available",
                        Predictions = new List<StockPrediction>()
                    });
                }

                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                _logger.LogInformation("Successfully generated predictions for {Count} stocks", predictions.Predictions?.Count ?? 0);

                return Ok(predictions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating predictions");
                return StatusCode(500, new { error = "Failed to generate predictions" });
            }
        }

        /// <summary>
        /// Manually sends daily prediction notification to all subscribers
        /// </summary>
        /// <returns>Success message</returns>
        [HttpPost("send-notification")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SendNotification()
        {
            try
            {
                _logger.LogInformation("Manually sending daily prediction notification");

                var stocks = await _stockService.GetIndianStockDataAsync();
                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stocks data available for notification");
                    return BadRequest(new { error = "No stock data available to send notification" });
                }

                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                await _notificationService.SendDailyPredictionReport(predictions);

                _logger.LogInformation("Daily prediction notification sent successfully");

                return Ok(new
                {
                    message = "Notification sent successfully",
                    timestamp = DateTime.UtcNow,
                    predictionsCount = predictions.Predictions?.Count ?? 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending notification");
                return StatusCode(500, new { error = "Failed to send notification" });
            }
        }

        /// <summary>
        /// Sends a test notification to verify email configuration
        /// </summary>
        /// <param name="request">Test notification request</param>
        /// <returns>Success message</returns>
        [HttpPost("test-notification")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TestNotification([FromBody] TestNotificationRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                _logger.LogInformation("Sending test notification");
                await _notificationService.SendTestNotification(request.Message);

                return Ok(new
                {
                    message = "Test notification sent successfully",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending test notification");
                return StatusCode(500, new { error = "Failed to send test notification" });
            }
        }
    }

    /// <summary>
    /// Request model for test notification
    /// </summary>
    public class TestNotificationRequest
    {
        public string Message { get; set; } = string.Empty;
    }
}