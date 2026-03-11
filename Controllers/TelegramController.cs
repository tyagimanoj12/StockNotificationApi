using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Diagnostics; // Added for Stopwatch

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
        private static readonly Stopwatch _uptimeStopwatch = Stopwatch.StartNew();
        private static readonly DateTime _startTime = DateTime.UtcNow;

        // Constants for validation
        private const int MAX_MESSAGE_LENGTH = 4000;
        private const int MAX_BROADCAST_BATCH_SIZE = 100;

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
                // Validate request
                if (request == null)
                {
                    return BadRequest(new { error = "Request cannot be null" });
                }

                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                if (request.Message.Length > MAX_MESSAGE_LENGTH)
                {
                    return BadRequest(new
                    {
                        error = $"Message too long. Maximum length is {MAX_MESSAGE_LENGTH} characters",
                        currentLength = request.Message.Length,
                        maxLength = MAX_MESSAGE_LENGTH
                    });
                }

                // Log broadcast (but don't log the full message if it's too long)
                var logMessage = request.Message.Length > 100
                    ? request.Message.Substring(0, 100) + "..."
                    : request.Message;

                _logger.LogInformation("Broadcasting message to all Telegram subscribers: {Message}", logMessage);

                // Execute broadcast
                await _telegramService.BroadcastToAllAsync(request.Message);

                return Ok(new
                {
                    message = "Broadcast sent successfully",
                    timestamp = DateTime.UtcNow,
                    contentLength = request.Message.Length,
                    estimatedRecipients = "All subscribers" // You could get actual count from service
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message");
                return StatusCode(500, new
                {
                    error = "Failed to broadcast message",
                    details = ex.Message
                });
            }
        }

        /// <summary>
        /// Sends a daily prediction report to all subscribed Telegram users
        /// </summary>
        /// <returns>Status of the report sending operation</returns>
        [HttpPost("send-report")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SendReport()
        {
            try
            {
                _logger.LogInformation("Generating and sending daily report to Telegram subscribers");

                // Fetch stock data
                var stocks = await _stockService.GetIndianStockDataAsync();
                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stock data available for report");
                    return BadRequest(new
                    {
                        error = "No stock data available",
                        message = "Unable to generate report without stock data"
                    });
                }

                _logger.LogInformation("Fetched {StockCount} stocks for analysis", stocks.Count);

                // Generate predictions
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);
                if (predictions == null)
                {
                    _logger.LogError("Failed to generate predictions");
                    return StatusCode(500, new { error = "Failed to generate predictions" });
                }

                // Get market insight
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                // Send report
                await _telegramService.SendDailyReportToAllAsync(predictions);

                _logger.LogInformation("Daily report sent successfully with {PredictionCount} predictions",
                    predictions.Predictions?.Count ?? 0);

                return Ok(new
                {
                    message = "Daily report sent successfully",
                    timestamp = DateTime.UtcNow,
                    predictionsCount = predictions.Predictions?.Count ?? 0,
                    topPick = predictions.TopPick,
                    marketSummary = predictions.MarketSummary?.Substring(0, Math.Min(100, predictions.MarketSummary?.Length ?? 0)) + "..."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending daily report");
                return StatusCode(500, new
                {
                    error = "Failed to send daily report",
                    details = ex.Message
                });
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

                // In a real implementation, these would come from the service
                // For now, we'll return what we can track
                var uptime = GetUptime();
                var uptimeString = FormatUptime(uptime);

                var stats = new
                {
                    status = "running",
                    timestamp = DateTime.UtcNow,
                    startTime = _startTime,
                    uptime = uptimeString,
                    uptimeSeconds = Math.Round(uptime.TotalSeconds, 0),
                    version = GetVersion(),
                    environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                    machineName = Environment.MachineName,
                    // These would come from your service if exposed
                    // subscribersCount = await _telegramService.GetSubscribersCountAsync(),
                    // briefingSubscribersCount = await _telegramService.GetBriefingSubscribersCountAsync(),
                    // activeToday = await _telegramService.GetActiveUsersTodayAsync()
                };

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching bot statistics");
                return StatusCode(500, new
                {
                    error = "Failed to fetch bot statistics",
                    details = ex.Message
                });
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
                // Validate request
                if (request == null)
                {
                    return BadRequest(new { error = "Request cannot be null" });
                }

                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new { error = "Message cannot be empty" });
                }

                if (request.ChatId <= 0)
                {
                    return BadRequest(new
                    {
                        error = "Valid chat ID is required",
                        message = "Chat ID must be a positive number"
                    });
                }

                if (request.Message.Length > MAX_MESSAGE_LENGTH)
                {
                    return BadRequest(new
                    {
                        error = $"Message too long. Maximum length is {MAX_MESSAGE_LENGTH} characters",
                        currentLength = request.Message.Length,
                        maxLength = MAX_MESSAGE_LENGTH
                    });
                }

                _logger.LogInformation("Sending direct message to chat {ChatId} (message length: {Length})",
                    request.ChatId, request.Message.Length);

                await _telegramService.SendMessageAsync(request.ChatId, request.Message);

                return Ok(new
                {
                    message = "Message sent successfully",
                    chatId = request.ChatId,
                    timestamp = DateTime.UtcNow,
                    messageLength = request.Message.Length
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending direct message to {ChatId}", request?.ChatId);
                return StatusCode(500, new
                {
                    error = "Failed to send message",
                    details = ex.Message
                });
            }
        }

        /// <summary>
        /// Gets bot health status (lightweight check)
        /// </summary>
        /// <returns>Bot health status</returns>
        [HttpGet("health")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public IActionResult Health()
        {
            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                uptime = FormatUptime(GetUptime())
            });
        }

        /// <summary>
        /// Gets the list of commands supported by the bot
        /// </summary>
        /// <returns>List of bot commands</returns>
        [HttpGet("commands")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public IActionResult GetCommands()
        {
            var commands = new[]
            {
                new { command = "/start", description = "Start the bot and get welcome message" },
                new { command = "/help", description = "Show all available commands" },
                new { command = "/stocks", description = "View top 10 active stocks" },
                new { command = "/topgainers", description = "View top 5 gainers today" },
                new { command = "/toplosers", description = "View top 5 losers today" },
                new { command = "/price SYMBOL", description = "Get price for a symbol (e.g., /price RELIANCE)" },
                new { command = "/alert SYMBOL PRICE above/below", description = "Set price alert" },
                new { command = "/alerts", description = "View your active alerts" },
                new { command = "/briefing", description = "Get today's AI-powered briefing" },
                new { command = "/market", description = "Check if market is open" },
                new { command = "/subscribe", description = "Subscribe to daily updates" },
                new { command = "/unsubscribe", description = "Unsubscribe from daily updates" }
            };

            return Ok(new
            {
                totalCommands = commands.Length,
                commands = commands,
                timestamp = DateTime.UtcNow
            });
        }

        #region Private Helper Methods

        private TimeSpan GetUptime()
        {
            return _uptimeStopwatch.Elapsed;
        }

        private string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
                return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalHours >= 1)
                return $"{uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalMinutes >= 1)
                return $"{uptime.Minutes}m {uptime.Seconds}s";

            return $"{uptime.Seconds}s";
        }

        private string GetVersion()
        {
            // Get version from assembly
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version != null ? version.ToString() : "1.0.0";
        }

        #endregion
    }

    /// <summary>
    /// Request model for broadcasting a message
    /// </summary>
    public class BroadcastRequest
    {
        /// <summary>
        /// The message to broadcast to all subscribers
        /// </summary>
        /// <example>Important market update: Nifty hits new high!</example>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request model for sending a direct message
    /// </summary>
    public class DirectMessageRequest
    {
        /// <summary>
        /// Telegram chat ID of the recipient
        /// </summary>
        /// <example>123456789</example>
        public long ChatId { get; set; }

        /// <summary>
        /// The message to send
        /// </summary>
        /// <example>Your price alert for RELIANCE was triggered!</example>
        public string Message { get; set; } = string.Empty;
    }
}