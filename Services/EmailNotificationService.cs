using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using StockNotificationApi.Constants;
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

        // Use constants from Constants.cs
        private const int SMTP_DEFAULT_PORT = 587;
        private const int RATE_LIMIT_DELAY_MS = RateLimitConstants.RATE_LIMIT_DELAY_MS;
        private const int MAX_RECIPIENTS_PER_EMAIL = 50;
        private const int MAX_RETRY_ATTEMPTS = TimeoutConstants.MAX_RETRY_ATTEMPTS;
        private const int RETRY_DELAY_MS = TimeoutConstants.RETRY_DELAY_MS;

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
            if (string.IsNullOrWhiteSpace(_emailSettings.SmtpServer))
                throw new InvalidOperationException("SMTP server not configured");

            if (_emailSettings.SmtpPort <= 0 || _emailSettings.SmtpPort > 65535)
                _emailSettings.SmtpPort = SMTP_DEFAULT_PORT;

            if (string.IsNullOrWhiteSpace(_emailSettings.SenderEmail))
                throw new InvalidOperationException("Sender email not configured");

            if (string.IsNullOrWhiteSpace(_emailSettings.SenderPassword))
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

            var recipients = _emailSettings.RecipientEmails
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!recipients.Any())
            {
                _logger.LogWarning("No valid recipients found after trimming");
                return;
            }

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting to send daily prediction report to {Count} recipients",
                recipients.Count);

            try
            {
                // Build common parts of the message
                var subject = $"📈 Indian Stock Market Predictions - {report.Date:dd MMM yyyy}";

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = BuildHtmlEmailBody(report),
                    TextBody = BuildTextEmailBody(report)
                };

                // Split into batches
                var batches = recipients
                    .Select((email, index) => new { email, index })
                    .GroupBy(x => x.index / MAX_RECIPIENTS_PER_EMAIL)
                    .Select(g => g.Select(x => x.email).ToList())
                    .ToList();

                var batchNumber = 0;
                foreach (var batch in batches)
                {
                    batchNumber++;

                    var message = new MimeMessage();
                    message.From.Add(new MailboxAddress("Stock Predictions", _emailSettings.SenderEmail));
                    foreach (var recipient in batch)
                    {
                        message.To.Add(new MailboxAddress(string.Empty, recipient));
                    }

                    message.Subject = subject;
                    message.Body = bodyBuilder.ToMessageBody();

                    _logger.LogInformation("Sending batch {BatchNumber}/{TotalBatches} with {RecipientCount} recipients",
                        batchNumber, batches.Count, batch.Count);

                    await SendWithRetryAsync(message);

                    // rate limit between batches
                    if (batchNumber < batches.Count)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("Daily prediction report sent successfully to {Count} recipients in {Duration}ms",
                    recipients.Count, duration.TotalMilliseconds);
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

            var firstRecipient = _emailSettings.RecipientEmails.FirstOrDefault(r => !string.IsNullOrEmpty(r))?.Trim();
            if (firstRecipient == null)
            {
                _logger.LogError("No valid recipients found for test notification");
                throw new InvalidOperationException("No valid recipients found");
            }

            try
            {
                var mimeMessage = new MimeMessage();
                mimeMessage.From.Add(new MailboxAddress("Stock Notifications", _emailSettings.SenderEmail));
                mimeMessage.To.Add(new MailboxAddress(string.Empty, firstRecipient));
                mimeMessage.Subject = "Test Notification";
                mimeMessage.Body = new TextPart("plain") { Text = message };

                // Use the same retry logic for the test message
                await SendWithRetryAsync(mimeMessage);

                _logger.LogInformation("Test notification sent successfully to {Recipient}", firstRecipient);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test notification to {Recipient}", firstRecipient);
                throw;
            }
        }

        private async Task SendWithRetryAsync(MimeMessage message, int maxRetries = 3)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            Exception? lastException = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var client = new SmtpClient
                    {
                        Timeout = TimeoutConstants.SMTP_TIMEOUT_MS // 30 second timeout
                    };

                    var socketOptions = GetSocketOptions();

                    await client.ConnectAsync(_emailSettings.SmtpServer, _emailSettings.SmtpPort, socketOptions);
                    _logger.LogDebug("Connected to SMTP server {Server}:{Port} (Attempt {Attempt})",
                        _emailSettings.SmtpServer, _emailSettings.SmtpPort, attempt);

                    await client.AuthenticateAsync(_emailSettings.SenderEmail, _emailSettings.SenderPassword);
                    _logger.LogDebug("Authenticated with SMTP server (Attempt {Attempt})", attempt);

                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);

                    // Success
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "SMTP send failed (attempt {Attempt}/{MaxRetries}), will retry...", attempt, maxRetries);
                    await Task.Delay(RATE_LIMIT_DELAY_MS * attempt); // backoff
                }
                catch (Exception ex)
                {
                    // Last attempt failed
                    lastException = ex;
                    _logger.LogError(ex, "SMTP send failed on final attempt ({Attempt}/{MaxRetries})", attempt, maxRetries);
                }
            }

            // If we reach here, all attempts failed
            throw new InvalidOperationException("Failed to send email after multiple attempts", lastException);
        }

        private SecureSocketOptions GetSocketOptions()
        {
            // If EnableSsl is true and port is 465, use SslOnConnect; otherwise use StartTls for TLS-enabled servers.
            return _emailSettings.EnableSsl
                ? (_emailSettings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
                : SecureSocketOptions.None;
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