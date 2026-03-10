using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TelegramController : ControllerBase
    {
        private readonly ITelegramBotService _telegramService;
        private readonly IStockService _stockService;
        private readonly IAIService _aiService;
        private readonly ILogger<TelegramController> _logger;

        public TelegramController(
            ITelegramBotService telegramService,
            IStockService stockService,
            IAIService aiService,
            ILogger<TelegramController> logger)
        {
            _telegramService = telegramService ?? throw new ArgumentNullException(nameof(telegramService));
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Broadcasts a message to all subscribed Telegram users
        /// </summary>
        /// <param name="request">The broadcast request containing the message</param>
        /// <returns>Status of the broadcast operation</returns>
        [HttpPost("broadcast")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                _logger.LogInformation("Broadcasting message to all Telegram subscribers: {Message}", request.Message);

                await _telegramService.BroadcastToAllAsync(request.Message);

                return Ok(new
                {
                    message = "Broadcast sent successfully",
                    timestamp = DateTime.UtcNow,
                    content = request.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message");
                return StatusCode(500, new { error = "Failed to broadcast message" });
            }
        }

        /// <summary>
        /// Sends a daily prediction report to all subscribed Telegram users
        /// </summary>
        /// <returns>Status of the report sending operation</returns>
        [HttpPost("send-report")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SendReport()
        {
            try
            {
                _logger.LogInformation("Generating and sending daily report to Telegram subscribers");

                var stocks = await _stockService.GetIndianStockDataAsync();
                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stock data available for report");
                    return BadRequest(new { error = "No stock data available" });
                }

                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                await _telegramService.SendDailyReportToAllAsync(predictions);

                return Ok(new
                {
                    message = "Daily report sent successfully",
                    timestamp = DateTime.UtcNow,
                    predictionsCount = predictions.Predictions?.Count ?? 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending daily report");
                return StatusCode(500, new { error = "Failed to send daily report" });
            }
        }

        /// <summary>
        /// Gets statistics about the Telegram bot
        /// </summary>
        /// <returns>Bot statistics including subscriber counts</returns>
        [HttpGet("stats")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetStats()
        {
            try
            {
                _logger.LogInformation("Fetching Telegram bot statistics");

                // Note: You would need to expose these counts from your service
                // For now, returning basic status
                var stats = new
                {
                    status = "running",
                    timestamp = DateTime.UtcNow,
                    version = "1.0.0",
                    uptime = GetUptime(),
                    // These would come from your service if exposed
                    // subscribersCount = await _telegramService.GetSubscribersCountAsync(),
                    // briefingSubscribersCount = await _telegramService.GetBriefingSubscribersCountAsync()
                };

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching bot statistics");
                return StatusCode(500, new { error = "Failed to fetch bot statistics" });
            }
        }

        /// <summary>
        /// Sends a message to a specific Telegram user
        /// </summary>
        /// <param name="request">The direct message request</param>
        /// <returns>Status of the message sending operation</returns>
        [HttpPost("send-message")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SendDirectMessage([FromBody] DirectMessageRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                if (request.ChatId <= 0)
                {
                    return BadRequest(new { error = "Valid chat ID is required" });
                }

                _logger.LogInformation("Sending direct message to chat {ChatId}", request.ChatId);

                await _telegramService.SendMessageAsync(request.ChatId, request.Message);

                return Ok(new
                {
                    message = "Message sent successfully",
                    chatId = request.ChatId,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending direct message to {ChatId}", request?.ChatId);
                return StatusCode(500, new { error = "Failed to send message" });
            }
        }

        private TimeSpan GetUptime()
        {
            // You would need to store the start time somewhere
            // For now, returning a placeholder
            return TimeSpan.FromHours(1);
        }
    }

    /// <summary>
    /// Request model for broadcasting a message
    /// </summary>
    public class BroadcastRequest
    {
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request model for sending a direct message
    /// </summary>
    public class DirectMessageRequest
    {
        public long ChatId { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}