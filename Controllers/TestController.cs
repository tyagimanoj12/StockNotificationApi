using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Models;
using MimeKit;
using MailKit.Net.Smtp;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestController : ControllerBase
    {
        private readonly IStockService _stockService;
        private readonly IAIService _aiService;
        private readonly INotificationService _notificationService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TestController> _logger;

        // Constants
        private const int PRICE_DISPLAY_PRECISION = 2;

        public TestController(
            IStockService stockService,
            IAIService aiService,
            INotificationService notificationService,
            IConfiguration configuration,
            ILogger<TestController> logger)
        {
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Tests the AI suggestion generation for all stocks
        /// </summary>
        [HttpGet("test-suggestions")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TestSuggestions()
        {
            try
            {
                _logger.LogInformation("Step 1: Fetching stock data...");
                var stocks = await _stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stock data available");
                    return BadRequest(new { error = "No stock data available", timestamp = DateTime.UtcNow });
                }

                _logger.LogInformation("Step 2: Generating AI predictions for {Count} stocks...", stocks.Count);
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);

                if (predictions == null)
                {
                    _logger.LogError("Failed to generate predictions");
                    return StatusCode(500, new { error = "Failed to generate predictions" });
                }

                _logger.LogInformation("Step 3: Getting market insight...");
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                var result = new
                {
                    timestamp = DateTime.UtcNow,
                    marketSummary = predictions.MarketSummary,
                    topPick = predictions.TopPick,
                    predictions = predictions.Predictions?.Select(p => new
                    {
                        symbol = p.Symbol,
                        company = p.CompanyName,
                        price = p.CurrentPrice,
                        prediction = p.Prediction,
                        recommendation = p.Recommendation,
                        confidence = p.Confidence,
                        reasons = p.KeyFactors,
                        outlook = p.ShortTermOutlook,
                        risk = p.RiskLevel
                    })
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in test suggestions");
                return StatusCode(500, new { error = ex.Message, timestamp = DateTime.UtcNow });
            }
        }

        /// <summary>
        /// Tests AI suggestion for a specific stock
        /// </summary>
        /// <param name="symbol">Stock symbol (e.g., RELIANCE.NS)</param>
        [HttpGet("test-specific/{symbol}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TestSpecificStock(string symbol)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    return BadRequest(new { error = "Symbol cannot be empty" });
                }

                _logger.LogInformation("Fetching data for {Symbol}", symbol);

                var stock = await _stockService.GetStockDataAsync(symbol.ToUpper());
                if (stock == null)
                {
                    _logger.LogWarning("Stock not found: {Symbol}", symbol);
                    return NotFound(new { error = $"Stock {symbol} not found" });
                }

                var predictions = await _aiService.GeneratePredictionsAsync(new List<StockData> { stock });
                var prediction = predictions?.Predictions?.FirstOrDefault();

                return Ok(new
                {
                    timestamp = DateTime.UtcNow,
                    stock = new
                    {
                        symbol = stock.Symbol,
                        name = stock.Name,
                        price = stock.Price,
                        change = stock.ChangePercent,
                        dayHigh = stock.DayHigh,
                        dayLow = stock.DayLow,
                        volume = stock.Volume,
                        exchange = stock.Exchange
                    },
                    aiSuggestion = prediction == null ? null : new
                    {
                        prediction = prediction.Prediction,
                        recommendation = prediction.Recommendation,
                        confidence = prediction.Confidence,
                        reasons = prediction.KeyFactors,
                        outlook = prediction.ShortTermOutlook,
                        risk = prediction.RiskLevel
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing {Symbol}", symbol);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Tests the full notification flow with optional email override
        /// </summary>
        /// <param name="email">Optional email address to send test to</param>
        [HttpPost("test-full-notification")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TestFullNotification([FromQuery] string? email = null)
        {
            try
            {
                _logger.LogInformation("Step 1: Fetching stock data...");
                var stocks = await _stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    _logger.LogWarning("No stock data available");
                    return BadRequest(new { error = "No stock data available", timestamp = DateTime.UtcNow });
                }

                _logger.LogInformation("Step 2: Generating AI predictions...");
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);

                if (predictions == null)
                {
                    _logger.LogError("Failed to generate predictions");
                    return StatusCode(500, new { error = "Failed to generate predictions" });
                }

                _logger.LogInformation("Step 3: Getting market insight...");
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                _logger.LogInformation("Step 4: Sending email notification...");

                if (!string.IsNullOrEmpty(email))
                {
                    // Send to specific test email
                    await SendTestEmail(predictions, email);
                }
                else
                {
                    // Send to configured recipients
                    await _notificationService.SendDailyPredictionReport(predictions);
                }

                return Ok(new
                {
                    message = "Test notification completed",
                    timestamp = DateTime.UtcNow,
                    steps = new
                    {
                        stockDataFetched = stocks.Count,
                        predictionsGenerated = predictions.Predictions?.Count ?? 0,
                        marketSummary = predictions.MarketSummary,
                        topPick = predictions.TopPick,
                        emailSent = true,
                        recipient = string.IsNullOrEmpty(email) ? "Configured recipients" : email
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in test notification");
                return StatusCode(500, new { error = ex.Message, timestamp = DateTime.UtcNow });
            }
        }

        /// <summary>
        /// Tests email configuration with custom parameters
        /// </summary>
        [HttpPost("test-email-config")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TestEmailConfig([FromBody] EmailTestRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.ToEmail))
                {
                    return BadRequest(new { error = "Valid recipient email is required" });
                }

                _logger.LogInformation("Testing email configuration for {ToEmail}", request.ToEmail);

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Stock Test",
                    !string.IsNullOrEmpty(request.FromEmail) ? request.FromEmail : _configuration["EmailSettings:SenderEmail"]));
                message.To.Add(new MailboxAddress("", request.ToEmail));
                message.Subject = request.Subject ?? "Test Email from Stock API";

                var bodyBuilder = new BodyBuilder
                {
                    TextBody = request.Message ?? "This is a test email from Stock Prediction API.",
                    HtmlBody = $"<h1>Test Email</h1><p>{request.Message ?? "This is a test email from Stock Prediction API."}</p>"
                };

                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();

                var smtpServer = _configuration["EmailSettings:SmtpServer"];
                var smtpPort = _configuration["EmailSettings:SmtpPort"];
                var senderEmail = _configuration["EmailSettings:SenderEmail"];
                var senderPassword = _configuration["EmailSettings:SenderPassword"];

                if (string.IsNullOrEmpty(smtpServer) || string.IsNullOrEmpty(smtpPort))
                {
                    return StatusCode(500, new { error = "SMTP configuration is incomplete" });
                }

                await client.ConnectAsync(
                    smtpServer,
                    int.Parse(smtpPort),
                    MailKit.Security.SecureSocketOptions.StartTls
                );

                if (!string.IsNullOrEmpty(senderEmail) && !string.IsNullOrEmpty(senderPassword))
                {
                    await client.AuthenticateAsync(senderEmail, senderPassword);
                }

                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Test email sent successfully to {ToEmail}", request.ToEmail);

                return Ok(new
                {
                    message = "Test email sent successfully",
                    to = request.ToEmail,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test email to {ToEmail}", request?.ToEmail);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Checks the configuration status of all services
        /// </summary>
        [HttpGet("check-config")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public IActionResult CheckConfig()
        {
            var emailSettings = new
            {
                SmtpServer = _configuration["EmailSettings:SmtpServer"],
                SmtpPort = _configuration["EmailSettings:SmtpPort"],
                SenderEmail = _configuration["EmailSettings:SenderEmail"],
                HasPassword = !string.IsNullOrEmpty(_configuration["EmailSettings:SenderPassword"]),
                Recipients = _configuration.GetSection("EmailSettings:RecipientEmails").Get<List<string>>() ?? new List<string>(),
                IsValid = !string.IsNullOrEmpty(_configuration["EmailSettings:SmtpServer"]) &&
                         !string.IsNullOrEmpty(_configuration["EmailSettings:SmtpPort"]) &&
                         !string.IsNullOrEmpty(_configuration["EmailSettings:SenderEmail"])
            };

            var stockSettings = new
            {
                ApiKey = !string.IsNullOrEmpty(_configuration["StockApiSettings:AlphaVantageApiKey"]),
                StockCount = _configuration.GetSection("StockApiSettings:IndianStocks").Get<List<string>>()?.Count ?? 0,
                IsValid = true // Always valid as we have fallbacks
            };

            var geminiSettings = new
            {
                HasApiKey = !string.IsNullOrEmpty(_configuration["GeminiAISettings:ApiKey"]),
                ApiUrl = _configuration["GeminiAISettings:ApiUrl"],
                IsValid = !string.IsNullOrEmpty(_configuration["GeminiAISettings:ApiKey"])
            };

            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                email = emailSettings,
                stock = stockSettings,
                gemini = geminiSettings,
                isConfigured = emailSettings.IsValid && geminiSettings.IsValid
            });
        }

        #region Private Helper Methods

        private async Task SendTestEmail(DailyPredictionReport report, string testEmail)
        {
            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Stock Predictions", _configuration["EmailSettings:SenderEmail"]));
                message.To.Add(new MailboxAddress("", testEmail));
                message.Subject = $"📈 TEST: Stock Predictions - {report.Date:dd MMM yyyy}";

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = BuildTestHtmlBody(report),
                    TextBody = BuildTestTextBody(report)
                };

                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();

                var smtpServer = _configuration["EmailSettings:SmtpServer"];
                var smtpPort = _configuration["EmailSettings:SmtpPort"];
                var senderEmail = _configuration["EmailSettings:SenderEmail"];
                var senderPassword = _configuration["EmailSettings:SenderPassword"];

                await client.ConnectAsync(
                    smtpServer,
                    int.Parse(smtpPort!),
                    MailKit.Security.SecureSocketOptions.StartTls
                );

                if (!string.IsNullOrEmpty(senderEmail) && !string.IsNullOrEmpty(senderPassword))
                {
                    await client.AuthenticateAsync(senderEmail, senderPassword);
                }

                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Test email sent to {Email}", testEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test email to {Email}", testEmail);
                throw;
            }
        }

        private string BuildTestHtmlBody(DailyPredictionReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; padding: 20px; line-height: 1.6; }");
            sb.AppendLine(".header { background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 20px; border-radius: 10px; }");
            sb.AppendLine(".summary { background: #f8f9fa; padding: 15px; border-left: 4px solid #667eea; margin: 20px 0; border-radius: 5px; }");
            sb.AppendLine(".stock-card { border: 1px solid #e0e0e0; padding: 15px; margin: 10px 0; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".bullish { color: #28a745; font-weight: bold; }");
            sb.AppendLine(".bearish { color: #dc3545; font-weight: bold; }");
            sb.AppendLine(".neutral { color: #ffc107; font-weight: bold; }");
            sb.AppendLine(".buy { background: #28a745; color: white; padding: 3px 10px; border-radius: 15px; display: inline-block; }");
            sb.AppendLine(".sell { background: #dc3545; color: white; padding: 3px 10px; border-radius: 15px; display: inline-block; }");
            sb.AppendLine(".hold { background: #ffc107; color: black; padding: 3px 10px; border-radius: 15px; display: inline-block; }");
            sb.AppendLine(".high { color: #28a745; } .medium { color: #ffc107; } .low { color: #dc3545; }");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine($"<div class='header'>");
            sb.AppendLine($"<h1>📈 TEST: Indian Stock Market Predictions</h1>");
            sb.AppendLine($"<h2>{report.Date:dddd, MMMM d, yyyy}</h2>");
            sb.AppendLine("</div>");

            sb.AppendLine($"<div class='summary'>");
            sb.AppendLine($"<h3>📊 Market Summary</h3>");
            sb.AppendLine($"<p>{report.MarketSummary}</p>");
            sb.AppendLine($"<p><strong>🏆 Top Pick:</strong> {report.TopPick}</p>");
            sb.AppendLine("</div>");

            sb.AppendLine("<h3>📈 Stock Predictions</h3>");

            if (report.Predictions != null)
            {
                foreach (var stock in report.Predictions.Where(p => p != null))
                {
                    sb.AppendLine($"<div class='stock-card'>");
                    sb.AppendLine($"<h4>{stock.CompanyName} ({stock.Symbol})</h4>");
                    sb.AppendLine($"<p><strong>Current Price:</strong> ₹{stock.CurrentPrice:F2}</p>");
                    sb.AppendLine($"<p><strong>Prediction:</strong> <span class='{stock.Prediction?.ToLower() ?? "neutral"}'>{stock.Prediction ?? "Neutral"}</span></p>");
                    sb.AppendLine($"<p><strong>Recommendation:</strong> <span class='{stock.Recommendation?.ToLower() ?? "hold"}'>{stock.Recommendation ?? "Hold"}</span></p>");
                    sb.AppendLine($"<p><strong>Confidence:</strong> <span class='{stock.Confidence?.ToLower() ?? "medium"}-confidence'>{stock.Confidence ?? "Medium"}</span></p>");

                    if (stock.KeyFactors?.Any() == true)
                    {
                        sb.AppendLine("<p><strong>Key Factors:</strong></p><ul>");
                        foreach (var factor in stock.KeyFactors.Where(f => !string.IsNullOrEmpty(f)))
                        {
                            sb.AppendLine($"<li>{factor}</li>");
                        }
                        sb.AppendLine("</ul>");
                    }

                    sb.AppendLine($"<p><strong>Outlook:</strong> {stock.ShortTermOutlook}</p>");
                    sb.AppendLine($"<p><strong>Risk Level:</strong> <span class='{stock.RiskLevel?.ToLower() ?? "medium"}'>{stock.RiskLevel ?? "Medium"}</span></p>");
                    sb.AppendLine("</div>");
                }
            }

            sb.AppendLine("<hr>");
            sb.AppendLine("<p><em>⚠️ This is a TEST message. These predictions are AI-generated and should not be considered as financial advice.</em></p>");
            sb.AppendLine("</body></html>");

            return sb.ToString();
        }

        private string BuildTestTextBody(DailyPredictionReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=".PadRight(60, '='));
            sb.AppendLine("INDIAN STOCK MARKET PREDICTIONS - TEST");
            sb.AppendLine($"Date: {report.Date:dddd, MMMM d, yyyy}");
            sb.AppendLine("=".PadRight(60, '='));

            sb.AppendLine($"\nMARKET SUMMARY");
            sb.AppendLine("-".PadRight(30, '-'));
            sb.AppendLine(report.MarketSummary ?? "No summary available");
            sb.AppendLine($"\nTop Pick: {report.TopPick ?? "None"}");

            sb.AppendLine($"\nSTOCK PREDICTIONS");
            sb.AppendLine("-".PadRight(30, '-'));

            if (report.Predictions != null)
            {
                foreach (var stock in report.Predictions.Where(p => p != null))
                {
                    sb.AppendLine($"\n{stock.CompanyName} ({stock.Symbol})");
                    sb.AppendLine($"Price: ₹{stock.CurrentPrice:F2}");
                    sb.AppendLine($"Prediction: {stock.Prediction ?? "Neutral"}");
                    sb.AppendLine($"Recommendation: {stock.Recommendation ?? "Hold"} (Confidence: {stock.Confidence ?? "Medium"})");

                    if (stock.KeyFactors?.Any() == true)
                    {
                        sb.AppendLine("Key Factors:");
                        foreach (var factor in stock.KeyFactors.Where(f => !string.IsNullOrEmpty(f)))
                        {
                            sb.AppendLine($"  • {factor}");
                        }
                    }

                    sb.AppendLine($"Outlook: {stock.ShortTermOutlook}");
                    sb.AppendLine($"Risk: {stock.RiskLevel ?? "Medium"}");
                    sb.AppendLine("-".PadRight(40, '-'));
                }
            }

            sb.AppendLine($"\n\n⚠️ This is a TEST message. Not financial advice.");

            return sb.ToString();
        }

        #endregion
    }

    public class EmailTestRequest
    {
        public string ToEmail { get; set; } = string.Empty;
        public string? FromEmail { get; set; }
        public string? Subject { get; set; }
        public string? Message { get; set; }
    }
}