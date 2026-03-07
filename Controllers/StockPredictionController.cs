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
            _stockService = stockService;
            _aiService = aiService;
            _notificationService = notificationService;
            _logger = logger;
        }

        [HttpGet("stocks")]
        public async Task<IActionResult> GetIndianStocks()
        {
            try
            {
                var stocks = await _stockService.GetIndianStockDataAsync();
                return Ok(stocks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching stocks");
                return StatusCode(500, new { error = "Failed to fetch stock data" });
            }
        }

        [HttpGet("stock/{symbol}")]
        public async Task<IActionResult> GetStock(string symbol)
        {
            try
            {
                var stock = await _stockService.GetStockDataAsync(symbol);
                if (stock == null)
                    return NotFound(new { error = "Stock not found" });

                return Ok(stock);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching stock {Symbol}", symbol);
                return StatusCode(500, new { error = "Failed to fetch stock data" });
            }
        }

        [HttpGet("predictions")]
        public async Task<IActionResult> GetPredictions()
        {
            try
            {
                var stocks = await _stockService.GetIndianStockDataAsync();
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                return Ok(predictions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating predictions");
                return StatusCode(500, new { error = "Failed to generate predictions" });
            }
        }

        [HttpPost("send-notification")]
        public async Task<IActionResult> SendNotification()
        {
            try
            {
                var stocks = await _stockService.GetIndianStockDataAsync();
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                await _notificationService.SendDailyPredictionReport(predictions);

                return Ok(new { message = "Notification sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending notification");
                return StatusCode(500, new { error = "Failed to send notification" });
            }
        }

        [HttpPost("test-notification")]
        public async Task<IActionResult> TestNotification([FromBody] string message)
        {
            try
            {
                await _notificationService.SendTestNotification(message);
                return Ok(new { message = "Test notification sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending test notification");
                return StatusCode(500, new { error = "Failed to send test notification" });
            }
        }
    }
}