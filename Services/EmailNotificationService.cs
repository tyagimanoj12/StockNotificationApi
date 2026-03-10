using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;

namespace StockNotificationApi.Services
{
    public class EmailNotificationService : INotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailNotificationService> _logger;
        private readonly EmailSettings _emailSettings;

        // Constants
        private const int SMTP_DEFAULT_PORT = 587;
        private const int RATE_LIMIT_DELAY_MS = 1000;
        private const int MAX_RECIPIENTS_PER_EMAIL = 50;

        public EmailNotificationService(
            IConfiguration configuration,
            ILogger<EmailNotificationService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _emailSettings = configuration.GetSection("EmailSettings").Get<EmailSettings>()
                ?? throw new ArgumentNullException(nameof(configuration), "EmailSettings not configured");

            ValidateEmailSettings();
        }

        private void ValidateEmailSettings()
        {
            if (string.IsNullOrEmpty(_emailSettings.SmtpServer))
                throw new InvalidOperationException("SMTP server not configured");

            if (_emailSettings.SmtpPort <= 0 || _emailSettings.SmtpPort > 65535)
                _emailSettings.SmtpPort = SMTP_DEFAULT_PORT;

            if (string.IsNullOrEmpty(_emailSettings.SenderEmail))
                throw new InvalidOperationException("Sender email not configured");

            if (string.IsNullOrEmpty(_emailSettings.SenderPassword))
                throw new InvalidOperationException("Sender password not configured");

            if (_emailSettings.RecipientEmails == null || !_emailSettings.RecipientEmails.Any())
                _logger.LogWarning("No recipient emails configured");
        }

        public async Task SendDailyPredictionReport(DailyPredictionReport report)
        {
            if (report == null)
            {
                _logger.LogError("Cannot send null report");
                throw new ArgumentNullException(nameof(report));
            }

            if (_emailSettings.RecipientEmails == null || !_emailSettings.RecipientEmails.Any())
            {
                _logger.LogWarning("No recipients configured, skipping email send");
                return;
            }

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting to send daily prediction report to {Count} recipients",
                _emailSettings.RecipientEmails.Count);

            try
            {
                using var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Stock Predictions", _emailSettings.SenderEmail));

                // Add recipients in batches to avoid email client limitations
                var recipientCount = 0;
                foreach (var recipient in _emailSettings.RecipientEmails.Take(MAX_RECIPIENTS_PER_EMAIL))
                {
                    if (!string.IsNullOrEmpty(recipient))
                    {
                        message.To.Add(new MailboxAddress("", recipient.Trim()));
                        recipientCount++;
                    }
                }

                if (recipientCount == 0)
                {
                    _logger.LogWarning("No valid recipients found");
                    return;
                }

                message.Subject = $"📈 Indian Stock Market Predictions - {report.Date:dd MMM yyyy}";

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = BuildHtmlEmailBody(report),
                    TextBody = BuildTextEmailBody(report)
                };

                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient
                {
                    Timeout = 30000 // 30 second timeout
                };

                await SendWithRetryAsync(client, message);

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("Daily prediction report sent successfully to {Count} recipients in {Duration}ms",
                    recipientCount, duration.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send daily prediction report after {Duration}ms",
                    (DateTime.UtcNow - startTime).TotalMilliseconds);
                throw;
            }
        }

        public async Task SendTestNotification(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                _logger.LogError("Cannot send empty test message");
                throw new ArgumentException("Message cannot be empty", nameof(message));
            }

            if (_emailSettings.RecipientEmails == null || !_emailSettings.RecipientEmails.Any())
            {
                _logger.LogError("No recipients configured for test notification");
                throw new InvalidOperationException("No recipients configured");
            }

            var firstRecipient = _emailSettings.RecipientEmails.FirstOrDefault(r => !string.IsNullOrEmpty(r));
            if (firstRecipient == null)
            {
                _logger.LogError("No valid recipients found for test notification");
                throw new InvalidOperationException("No valid recipients found");
            }

            try
            {
                using var mimeMessage = new MimeMessage();
                mimeMessage.From.Add(new MailboxAddress("Stock Notifications", _emailSettings.SenderEmail));
                mimeMessage.To.Add(new MailboxAddress("", firstRecipient.Trim()));
                mimeMessage.Subject = "Test Notification";
                mimeMessage.Body = new TextPart("plain") { Text = message };

                using var client = new SmtpClient
                {
                    Timeout = 30000
                };

                await ConnectAndAuthenticateAsync(client);
                await client.SendAsync(mimeMessage);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Test notification sent successfully to {Recipient}", firstRecipient);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test notification to {Recipient}", firstRecipient);
                throw;
            }
        }

        private async Task SendWithRetryAsync(SmtpClient client, MimeMessage message, int maxRetries = 3)
        {
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                try
                {
                    await ConnectAndAuthenticateAsync(client);
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                    return;
                }
                catch (Exception ex) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    _logger.LogWarning(ex, "SMTP send failed (attempt {RetryCount}/{MaxRetries}), retrying...",
                        retryCount + 1, maxRetries);
                    await Task.Delay(RATE_LIMIT_DELAY_MS * retryCount); // Exponential backoff

                    // Reset client state
                    if (client.IsConnected)
                    {
                        await client.DisconnectAsync(true);
                    }
                }
            }
        }

        private async Task ConnectAndAuthenticateAsync(SmtpClient client)
        {
            if (!client.IsConnected)
            {
                await client.ConnectAsync(
                    _emailSettings.SmtpServer,
                    _emailSettings.SmtpPort,
                    SecureSocketOptions.StartTls);

                _logger.LogDebug("Connected to SMTP server {Server}:{Port}",
                    _emailSettings.SmtpServer, _emailSettings.SmtpPort);
            }

            if (!client.IsAuthenticated)
            {
                await client.AuthenticateAsync(_emailSettings.SenderEmail, _emailSettings.SenderPassword);
                _logger.LogDebug("Authenticated with SMTP server");
            }
        }

        private string BuildHtmlEmailBody(DailyPredictionReport report)
        {
            if (report == null) return string.Empty;

            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            sb.AppendLine("<style>");
            AppendStyles(sb);
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            // Header
            sb.AppendLine($"<h1>📊 Indian Stock Market Predictions</h1>");
            sb.AppendLine($"<h2>{report.Date:dddd, MMMM d, yyyy}</h2>");

            // Market Summary
            sb.AppendLine($"<div class='summary'>");
            sb.AppendLine($"<h3>Market Summary</h3>");
            sb.AppendLine($"<p>{EscapeHtml(report.MarketSummary ?? "No summary available")}</p>");
            sb.AppendLine($"<p><strong>Top Pick: {EscapeHtml(report.TopPick ?? "None")}</strong></p>");
            sb.AppendLine("</div>");

            // Stock Predictions
            if (report.Predictions?.Any() == true)
            {
                sb.AppendLine("<h3>Stock Predictions</h3>");

                foreach (var stock in report.Predictions)
                {
                    if (stock == null) continue;
                    AppendStockCard(sb, stock);
                }
            }

            // Disclaimer
            sb.AppendLine("<p class='disclaimer'><em>Disclaimer: These predictions are AI-generated and should not be considered as financial advice. Always do your own research before making investment decisions.</em></p>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private void AppendStyles(StringBuilder sb)
        {
            sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; line-height: 1.6; color: #333; }");
            sb.AppendLine("h1 { color: #2c3e50; border-bottom: 2px solid #3498db; padding-bottom: 10px; }");
            sb.AppendLine("h2 { color: #7f8c8d; font-size: 1.2em; margin-top: 5px; }");
            sb.AppendLine("h3 { color: #34495e; margin: 20px 0 10px; }");
            sb.AppendLine(".summary { background-color: #f8f9fa; padding: 20px; border-left: 4px solid #3498db; border-radius: 5px; margin-bottom: 25px; }");
            sb.AppendLine(".stock-card { border: 1px solid #e0e0e0; padding: 20px; margin-bottom: 20px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            sb.AppendLine(".stock-card h4 { color: #2c3e50; margin-top: 0; border-bottom: 1px solid #eee; padding-bottom: 10px; }");
            sb.AppendLine(".bullish { color: #27ae60; font-weight: bold; }");
            sb.AppendLine(".bearish { color: #e74c3c; font-weight: bold; }");
            sb.AppendLine(".neutral { color: #f39c12; font-weight: bold; }");
            sb.AppendLine(".high-confidence { color: #27ae60; }");
            sb.AppendLine(".medium-confidence { color: #f39c12; }");
            sb.AppendLine(".low-confidence { color: #e74c3c; }");
            sb.AppendLine(".factors { list-style-type: none; padding-left: 0; }");
            sb.AppendLine(".factors li { margin: 8px 0; padding-left: 20px; position: relative; }");
            sb.AppendLine(".factors li:before { content: '✓'; color: #27ae60; position: absolute; left: 0; }");
            sb.AppendLine(".disclaimer { font-size: 0.9em; color: #7f8c8d; margin-top: 30px; padding-top: 20px; border-top: 1px solid #eee; }");
        }

        private void AppendStockCard(StringBuilder sb, StockPrediction stock)
        {
            var sentimentClass = stock.Prediction?.ToLower() ?? "neutral";

            sb.AppendLine($"<div class='stock-card'>");
            sb.AppendLine($"<h4>{EscapeHtml(stock.CompanyName ?? stock.Symbol)} ({EscapeHtml(stock.Symbol)}) - ₹{stock.CurrentPrice:F2}</h4>");
            sb.AppendLine($"<p><strong>Prediction:</strong> <span class='{sentimentClass}'>{EscapeHtml(stock.Prediction ?? "Neutral")}</span></p>");
            sb.AppendLine($"<p><strong>Recommendation:</strong> {EscapeHtml(stock.Recommendation ?? "Hold")}</p>");

            if (!string.IsNullOrEmpty(stock.Confidence))
            {
                var confidenceClass = stock.Confidence.ToLower() + "-confidence";
                sb.AppendLine($"<p><strong>Confidence:</strong> <span class='{confidenceClass}'>{EscapeHtml(stock.Confidence)}</span></p>");
            }

            if (stock.KeyFactors?.Any() == true)
            {
                sb.AppendLine("<p><strong>Key Factors:</strong></p>");
                sb.AppendLine("<ul class='factors'>");
                foreach (var factor in stock.KeyFactors.Where(f => !string.IsNullOrEmpty(f)))
                {
                    sb.AppendLine($"<li>{EscapeHtml(factor)}</li>");
                }
                sb.AppendLine("</ul>");
            }

            if (!string.IsNullOrEmpty(stock.ShortTermOutlook))
            {
                sb.AppendLine($"<p><strong>Short-term Outlook:</strong> {EscapeHtml(stock.ShortTermOutlook)}</p>");
            }

            if (!string.IsNullOrEmpty(stock.RiskLevel))
            {
                sb.AppendLine($"<p><strong>Risk Level:</strong> {EscapeHtml(stock.RiskLevel)}</p>");
            }

            sb.AppendLine("</div>");
        }

        private string BuildTextEmailBody(DailyPredictionReport report)
        {
            if (report == null) return string.Empty;

            var sb = new StringBuilder();

            sb.AppendLine("INDIAN STOCK MARKET PREDICTIONS");
            sb.AppendLine($"Date: {report.Date:dddd, MMMM d, yyyy}");
            sb.AppendLine(new string('=', 50));

            sb.AppendLine($"\nMARKET SUMMARY");
            sb.AppendLine(new string('-', 20));
            sb.AppendLine(report.MarketSummary ?? "No summary available");
            sb.AppendLine($"\nTop Pick: {report.TopPick ?? "None"}");

            if (report.Predictions?.Any() == true)
            {
                sb.AppendLine($"\nSTOCK PREDICTIONS");
                sb.AppendLine(new string('-', 20));

                foreach (var stock in report.Predictions)
                {
                    if (stock == null) continue;

                    sb.AppendLine($"\n{stock.CompanyName ?? stock.Symbol} ({stock.Symbol})");
                    sb.AppendLine($"Current Price: ₹{stock.CurrentPrice:F2}");
                    sb.AppendLine($"Prediction: {stock.Prediction ?? "Neutral"}");
                    sb.AppendLine($"Recommendation: {stock.Recommendation ?? "Hold"}");
                    sb.AppendLine($"Confidence: {stock.Confidence ?? "Medium"}");

                    if (stock.KeyFactors?.Any() == true)
                    {
                        sb.AppendLine("Key Factors:");
                        foreach (var factor in stock.KeyFactors.Where(f => !string.IsNullOrEmpty(f)))
                        {
                            sb.AppendLine($"  • {factor}");
                        }
                    }

                    if (!string.IsNullOrEmpty(stock.ShortTermOutlook))
                    {
                        sb.AppendLine($"Short-term Outlook: {stock.ShortTermOutlook}");
                    }

                    if (!string.IsNullOrEmpty(stock.RiskLevel))
                    {
                        sb.AppendLine($"Risk Level: {stock.RiskLevel}");
                    }

                    sb.AppendLine(new string('-', 30));
                }
            }

            sb.AppendLine($"\n\nDisclaimer: These predictions are AI-generated and should not be considered as financial advice. Always do your own research before making investment decisions.");

            return sb.ToString();
        }

        private string EscapeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            return text.Replace("&", "&amp;")
                      .Replace("<", "&lt;")
                      .Replace(">", "&gt;")
                      .Replace("\"", "&quot;")
                      .Replace("'", "&#39;");
        }
    }
}