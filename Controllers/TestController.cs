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

        public TestController(
            IStockService stockService,
            IAIService aiService,
            INotificationService notificationService,
            IConfiguration configuration,
            ILogger<TestController> logger)
        {
            _stockService = stockService;
            _aiService = aiService;
            _notificationService = notificationService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("test-suggestions")]
        public async Task<IActionResult> TestSuggestions()
        {
            try
            {
                _logger.LogInformation("Step 1: Fetching stock data...");
                var stocks = await _stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    return BadRequest(new { error = "No stock data available" });
                }

                _logger.LogInformation("Step 2: Generating AI predictions for {Count} stocks...", stocks.Count);
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);

                _logger.LogInformation("Step 3: Getting market insight...");
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stocks);

                var result = new
                {
                    timestamp = DateTime.Now,
                    marketSummary = predictions.MarketSummary,
                    topPick = predictions.TopPick,
                    predictions = predictions.Predictions.Select(p => new
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
                return StatusCode(500, new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        [HttpGet("test-specific/{symbol}")]
        public async Task<IActionResult> TestSpecificStock(string symbol)
        {
            try
            {
                _logger.LogInformation("Fetching data for {Symbol}", symbol);

                var stock = await _stockService.GetStockDataAsync(symbol);
                if (stock == null)
                    return NotFound($"Stock {symbol} not found");

                var predictions = await _aiService.GeneratePredictionsAsync(new List<StockData> { stock });
                var prediction = predictions.Predictions.FirstOrDefault();

                return Ok(new
                {
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

        [HttpPost("test-full-notification")]
        public async Task<IActionResult> TestFullNotification([FromQuery] string? email = null)
        {
            try
            {
                _logger.LogInformation("Step 1: Fetching stock data...");
                var stocks = await _stockService.GetIndianStockDataAsync();

                if (stocks == null || !stocks.Any())
                {
                    return BadRequest(new { error = "No stock data available" });
                }

                _logger.LogInformation("Step 2: Generating AI predictions...");
                var predictions = await _aiService.GeneratePredictionsAsync(stocks);

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
                    steps = new
                    {
                        stockDataFetched = stocks.Count,
                        predictionsGenerated = predictions.Predictions.Count,
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
                return StatusCode(500, new { error = ex.Message, details = ex.StackTrace });
            }
        }

        [HttpPost("test-email-config")]
        public async Task<IActionResult> TestEmailConfig([FromBody] EmailTestRequest request)
        {
            try
            {
                _logger.LogInformation("Testing email configuration...");

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Stock Test", request.FromEmail ?? _configuration["EmailSettings:SenderEmail"]));
                message.To.Add(new MailboxAddress("", request.ToEmail));
                message.Subject = request.Subject ?? "Test Email from Stock API";

                var bodyBuilder = new BodyBuilder
                {
                    TextBody = request.Message ?? "This is a test email from Stock Prediction API.",
                    HtmlBody = $"<h1>Test Email</h1><p>{request.Message ?? "This is a test email from Stock Prediction API."}</p>"
                };

                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();
                await client.ConnectAsync(
                    _configuration["EmailSettings:SmtpServer"],
                    int.Parse(_configuration["EmailSettings:SmtpPort"]),
                    MailKit.Security.SecureSocketOptions.StartTls
                );

                await client.AuthenticateAsync(
                    _configuration["EmailSettings:SenderEmail"],
                    _configuration["EmailSettings:SenderPassword"]
                );

                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                return Ok(new { message = "Test email sent successfully", to = request.ToEmail });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test email");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("check-config")]
        public IActionResult CheckConfig()
        {
            var emailSettings = new
            {
                SmtpServer = _configuration["EmailSettings:SmtpServer"],
                SmtpPort = _configuration["EmailSettings:SmtpPort"],
                SenderEmail = _configuration["EmailSettings:SenderEmail"],
                HasPassword = !string.IsNullOrEmpty(_configuration["EmailSettings:SenderPassword"]),
                Recipients = _configuration.GetSection("EmailSettings:RecipientEmails").Get<List<string>>()
            };

            var stockSettings = new
            {
                ApiKey = !string.IsNullOrEmpty(_configuration["StockApiSettings:AlphaVantageApiKey"]),
                StockCount = _configuration.GetSection("StockApiSettings:IndianStocks").Get<List<string>>()?.Count ?? 0
            };

            var geminiSettings = new
            {
                HasApiKey = !string.IsNullOrEmpty(_configuration["GeminiAISettings:ApiKey"]),
                ApiUrl = _configuration["GeminiAISettings:ApiUrl"]
            };

            return Ok(new
            {
                timestamp = DateTime.Now,
                email = emailSettings,
                stock = stockSettings,
                gemini = geminiSettings,
                isConfigured = emailSettings.HasPassword && stockSettings.ApiKey && geminiSettings.HasApiKey
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
                await client.ConnectAsync(
                    _configuration["EmailSettings:SmtpServer"],
                    int.Parse(_configuration["EmailSettings:SmtpPort"]),
                    MailKit.Security.SecureSocketOptions.StartTls
                );

                await client.AuthenticateAsync(
                    _configuration["EmailSettings:SenderEmail"],
                    _configuration["EmailSettings:SenderPassword"]
                );

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
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; padding: 20px; }");
            sb.AppendLine(".header { background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 20px; border-radius: 10px; }");
            sb.AppendLine(".summary { background: #f8f9fa; padding: 15px; border-left: 4px solid #667eea; margin: 20px 0; }");
            sb.AppendLine(".stock-card { border: 1px solid #e0e0e0; padding: 15px; margin: 10px 0; border-radius: 8px; }");
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

            foreach (var stock in report.Predictions)
            {
                sb.AppendLine($"<div class='stock-card'>");
                sb.AppendLine($"<h4>{stock.CompanyName} ({stock.Symbol})</h4>");
                sb.AppendLine($"<p><strong>Current Price:</strong> ₹{stock.CurrentPrice:F2}</p>");
                sb.AppendLine($"<p><strong>Prediction:</strong> <span class='{stock.Prediction.ToLower()}'>{stock.Prediction}</span></p>");
                sb.AppendLine($"<p><strong>Recommendation:</strong> <span class='{stock.Recommendation.ToLower()}'>{stock.Recommendation}</span></p>");
                sb.AppendLine($"<p><strong>Confidence:</strong> <span class='{stock.Confidence.ToLower()}'>{stock.Confidence}</span></p>");
                sb.AppendLine("<p><strong>Key Factors:</strong></p><ul>");
                foreach (var factor in stock.KeyFactors)
                {
                    sb.AppendLine($"<li>{factor}</li>");
                }
                sb.AppendLine("</ul>");
                sb.AppendLine($"<p><strong>Outlook:</strong> {stock.ShortTermOutlook}</p>");
                sb.AppendLine($"<p><strong>Risk Level:</strong> <span class='{stock.RiskLevel.ToLower()}'>{stock.RiskLevel}</span></p>");
                sb.AppendLine("</div>");
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
            sb.AppendLine(report.MarketSummary);
            sb.AppendLine($"\nTop Pick: {report.TopPick}");

            sb.AppendLine($"\nSTOCK PREDICTIONS");
            sb.AppendLine("-".PadRight(30, '-'));

            foreach (var stock in report.Predictions)
            {
                sb.AppendLine($"\n{stock.CompanyName} ({stock.Symbol})");
                sb.AppendLine($"Price: ₹{stock.CurrentPrice:F2}");
                sb.AppendLine($"Prediction: {stock.Prediction}");
                sb.AppendLine($"Recommendation: {stock.Recommendation} (Confidence: {stock.Confidence})");
                sb.AppendLine("Key Factors:");
                foreach (var factor in stock.KeyFactors)
                {
                    sb.AppendLine($"  • {factor}");
                }
                sb.AppendLine($"Outlook: {stock.ShortTermOutlook}");
                sb.AppendLine($"Risk: {stock.RiskLevel}");
                sb.AppendLine("-".PadRight(40, '-'));
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