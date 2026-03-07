using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using StockNotificationApi.Models;
using System.Text;

namespace StockNotificationApi.Services
{
    public class EmailNotificationService : INotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailNotificationService> _logger;
        private readonly EmailSettings _emailSettings;

        public EmailNotificationService(
            IConfiguration configuration,
            ILogger<EmailNotificationService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _emailSettings = configuration.GetSection("EmailSettings").Get<EmailSettings>()
                ?? throw new ArgumentNullException("EmailSettings not configured");
        }

        public async Task SendDailyPredictionReport(DailyPredictionReport report)
        {
            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Stock Predictions", _emailSettings.SenderEmail));

                foreach (var recipient in _emailSettings.RecipientEmails)
                {
                    message.To.Add(new MailboxAddress("", recipient));
                }

                message.Subject = $"📈 Indian Stock Market Predictions - {report.Date:dd MMM yyyy}";

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = BuildHtmlEmailBody(report),
                    TextBody = BuildTextEmailBody(report)
                };

                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();

                // IMPORTANT: Use StartTls for port 587, NOT SslOnConnect
                await client.ConnectAsync(_emailSettings.SmtpServer, _emailSettings.SmtpPort, SecureSocketOptions.StartTls);

                // Authenticate using your Gmail app password
                await client.AuthenticateAsync(_emailSettings.SenderEmail, _emailSettings.SenderPassword);

                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Daily prediction report sent successfully to {Count} recipients",
                    _emailSettings.RecipientEmails.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send daily prediction report");
                throw;
            }
        }

        public async Task SendTestNotification(string message)
        {
            try
            {
                var mimeMessage = new MimeMessage();
                mimeMessage.From.Add(new MailboxAddress("Stock Notifications", _emailSettings.SenderEmail));
                mimeMessage.To.Add(new MailboxAddress("", _emailSettings.RecipientEmails.First()));
                mimeMessage.Subject = "Test Notification";
                mimeMessage.Body = new TextPart("plain") { Text = message };

                using var client = new SmtpClient();
                // IMPORTANT: Same fix applies here
                await client.ConnectAsync(_emailSettings.SmtpServer, _emailSettings.SmtpPort, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_emailSettings.SenderEmail, _emailSettings.SenderPassword);
                await client.SendAsync(mimeMessage);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Test notification sent successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send test notification");
                throw;
            }
        }

        private string BuildHtmlEmailBody(DailyPredictionReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
            sb.AppendLine("h1 { color: #333; }");
            sb.AppendLine("h2 { color: #666; }");
            sb.AppendLine(".summary { background-color: #f0f0f0; padding: 15px; border-radius: 5px; margin-bottom: 20px; }");
            sb.AppendLine(".stock-card { border: 1px solid #ddd; padding: 15px; margin-bottom: 15px; border-radius: 5px; }");
            sb.AppendLine(".bullish { color: green; font-weight: bold; }");
            sb.AppendLine(".bearish { color: red; font-weight: bold; }");
            sb.AppendLine(".neutral { color: orange; font-weight: bold; }");
            sb.AppendLine(".high-confidence { color: darkgreen; }");
            sb.AppendLine(".medium-confidence { color: orange; }");
            sb.AppendLine(".low-confidence { color: red; }");
            sb.AppendLine(".factors { list-style-type: none; padding-left: 0; }");
            sb.AppendLine(".factors li { margin: 5px 0; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine($"<h1>📊 Indian Stock Market Predictions</h1>");
            sb.AppendLine($"<h2>{report.Date:dddd, MMMM d, yyyy}</h2>");

            sb.AppendLine($"<div class='summary'>");
            sb.AppendLine($"<h3>Market Summary</h3>");
            sb.AppendLine($"<p>{report.MarketSummary}</p>");
            sb.AppendLine($"<p><strong>Top Pick: {report.TopPick}</strong></p>");
            sb.AppendLine("</div>");

            sb.AppendLine("<h3>Stock Predictions</h3>");

            foreach (var stock in report.Predictions)
            {
                var sentimentClass = stock.Prediction.ToLower();

                sb.AppendLine($"<div class='stock-card'>");
                sb.AppendLine($"<h4>{stock.CompanyName} ({stock.Symbol}) - ₹{stock.CurrentPrice:F2}</h4>");
                sb.AppendLine($"<p><strong>Prediction:</strong> <span class='{sentimentClass}'>{stock.Prediction}</span></p>");
                sb.AppendLine($"<p><strong>Recommendation:</strong> {stock.Recommendation}</p>");

                var confidenceClass = stock.Confidence.ToLower() + "-confidence";
                sb.AppendLine($"<p><strong>Confidence:</strong> <span class='{confidenceClass}'>{stock.Confidence}</span></p>");

                sb.AppendLine("<p><strong>Key Factors:</strong></p>");
                sb.AppendLine("<ul class='factors'>");
                foreach (var factor in stock.KeyFactors)
                {
                    sb.AppendLine($"<li>✓ {factor}</li>");
                }
                sb.AppendLine("</ul>");

                sb.AppendLine($"<p><strong>Short-term Outlook:</strong> {stock.ShortTermOutlook}</p>");
                sb.AppendLine($"<p><strong>Risk Level:</strong> {stock.RiskLevel}</p>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("<p><em>Disclaimer: These predictions are AI-generated and should not be considered as financial advice. Always do your own research before making investment decisions.</em></p>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private string BuildTextEmailBody(DailyPredictionReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"INDIAN STOCK MARKET PREDICTIONS");
            sb.AppendLine($"Date: {report.Date:dddd, MMMM d, yyyy}");
            sb.AppendLine(new string('=', 50));

            sb.AppendLine($"\nMARKET SUMMARY");
            sb.AppendLine(new string('-', 20));
            sb.AppendLine(report.MarketSummary);
            sb.AppendLine($"\nTop Pick: {report.TopPick}");

            sb.AppendLine($"\nSTOCK PREDICTIONS");
            sb.AppendLine(new string('-', 20));

            foreach (var stock in report.Predictions)
            {
                sb.AppendLine($"\n{stock.CompanyName} ({stock.Symbol})");
                sb.AppendLine($"Current Price: ₹{stock.CurrentPrice:F2}");
                sb.AppendLine($"Prediction: {stock.Prediction}");
                sb.AppendLine($"Recommendation: {stock.Recommendation}");
                sb.AppendLine($"Confidence: {stock.Confidence}");
                sb.AppendLine("Key Factors:");
                foreach (var factor in stock.KeyFactors)
                {
                    sb.AppendLine($"  • {factor}");
                }
                sb.AppendLine($"Short-term Outlook: {stock.ShortTermOutlook}");
                sb.AppendLine($"Risk Level: {stock.RiskLevel}");
                sb.AppendLine(new string('-', 30));
            }

            sb.AppendLine($"\n\nDisclaimer: These predictions are AI-generated and should not be considered as financial advice. Always do your own research before making investment decisions.");

            return sb.ToString();
        }
    }
}