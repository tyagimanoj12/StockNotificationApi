using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace StockNotificationApi.Services
{
    public class TwilioSmsService : ISmsNotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<TwilioSmsService> _logger;
        private readonly bool _isInitialized;

        // Use constants from Constants.cs
        private const int MAX_MESSAGE_LENGTH = SmsConstants.MAX_MESSAGE_LENGTH;
        private const int SMS_SEND_DELAY_MS = RateLimitConstants.SMS_SEND_DELAY_MS;
        private const int MAX_RETRY_ATTEMPTS = TimeoutConstants.MAX_RETRY_ATTEMPTS;

        public TwilioSmsService(IConfiguration configuration, ILogger<TwilioSmsService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _isInitialized = InitializeTwilioClient();
        }

        private bool InitializeTwilioClient()
        {
            try
            {
                var accountSid = _configuration["Twilio:AccountSid"];
                var authToken = _configuration["Twilio:AuthToken"];

                if (string.IsNullOrEmpty(accountSid) || string.IsNullOrEmpty(authToken))
                {
                    _logger.LogWarning("Twilio credentials not configured. SMS service will be disabled.");
                    return false;
                }

                TwilioClient.Init(accountSid, authToken);
                _logger.LogInformation("Twilio SMS service initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Twilio client");
                return false;
            }
        }

        public async Task SendSmsAsync(string to, string message)
        {
            if (!_isInitialized)
            {
                _logger.LogWarning("Twilio SMS service not initialized. Message not sent to {To}", to);
                return;
            }

            if (string.IsNullOrWhiteSpace(to))
            {
                _logger.LogWarning("Cannot send SMS to empty phone number");
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("Cannot send empty SMS message to {To}", to);
                return;
            }

            try
            {
                var from = _configuration["Twilio:FromNumber"];
                if (string.IsNullOrEmpty(from))
                {
                    _logger.LogError("Twilio from number not configured");
                    return;
                }

                // Format phone numbers
                var formattedTo = FormatPhoneNumber(to);
                var formattedFrom = FormatPhoneNumber(from);

                // Truncate message if too long
                var truncatedMessage = TruncateMessage(message);

                _logger.LogDebug("Sending SMS to {To} from {From}", formattedTo, formattedFrom);

                var messageResource = await MessageResource.CreateAsync(
                    body: truncatedMessage,
                    from: new PhoneNumber(formattedFrom),
                    to: new PhoneNumber(formattedTo)
                );

                _logger.LogInformation("SMS sent successfully to {To}, SID: {Sid}, Status: {Status}",
                    formattedTo, messageResource.Sid, messageResource.Status);
            }
            catch (TwilioException ex)
            {
                _logger.LogError(ex, "Twilio error sending SMS to {To}: {Message}", to, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending SMS to {To}", to);
                throw;
            }
        }

        public async Task SendSmsWithRetryAsync(string to, string message, int maxRetries = MAX_RETRY_ATTEMPTS)
        {
            int attempt = 0;
            while (attempt < maxRetries)
            {
                try
                {
                    await SendSmsAsync(to, message);
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    attempt++;
                    _logger.LogWarning(ex, "SMS send failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                        attempt + 1, maxRetries, SMS_SEND_DELAY_MS * attempt);
                    await Task.Delay(SMS_SEND_DELAY_MS * attempt);
                }
            }
        }

        public async Task SendBulkSmsAsync(List<string> recipients, string message)
        {
            if (recipients == null || !recipients.Any())
            {
                _logger.LogWarning("No recipients provided for bulk SMS");
                return;
            }

            _logger.LogInformation("Sending bulk SMS to {Count} recipients", recipients.Count);

            var successCount = 0;
            var failureCount = 0;

            foreach (var recipient in recipients.Distinct()) // Remove duplicates
            {
                try
                {
                    await SendSmsAsync(recipient, message);
                    successCount++;
                    await Task.Delay(SMS_SEND_DELAY_MS); // Rate limiting
                }
                catch (Exception ex)
                {
                    failureCount++;
                    _logger.LogError(ex, "Failed to send SMS to {Recipient}", recipient);
                }
            }

            _logger.LogInformation("Bulk SMS completed - Success: {Success}, Failures: {Failure}",
                successCount, failureCount);
        }

        public async Task SendPredictionAlertAsync(StockPrediction prediction)
        {
            if (prediction == null)
            {
                _logger.LogWarning("Cannot send null prediction alert");
                return;
            }

            try
            {
                var message = BuildPredictionMessage(prediction);
                var recipients = GetRecipients();

                if (!recipients.Any())
                {
                    _logger.LogWarning("No recipients configured for prediction alerts");
                    return;
                }

                await SendBulkSmsAsync(recipients, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending prediction alert for {Symbol}", prediction.Symbol);
                throw;
            }
        }

        public async Task SendMarketAlertAsync(string alertType, string message)
        {
            if (string.IsNullOrWhiteSpace(alertType) || string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("Invalid market alert parameters");
                return;
            }

            try
            {
                var formattedMessage = $"📊 {alertType}: {TruncateMessage(message, 150)}";
                var recipients = GetRecipients();

                if (!recipients.Any())
                {
                    _logger.LogWarning("No recipients configured for market alerts");
                    return;
                }

                await SendBulkSmsAsync(recipients, formattedMessage);
                _logger.LogInformation("Market alert '{AlertType}' sent to {Count} recipients",
                    alertType, recipients.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending market alert '{AlertType}'", alertType);
                throw;
            }
        }

        public async Task<bool> TestConnectionAsync()
        {
            if (!_isInitialized)
            {
                _logger.LogWarning("Cannot test connection - Twilio not initialized");
                return false;
            }

            try
            {
                // Try to fetch account details as a connection test
                var account = await Twilio.Rest.Api.V2010.AccountResource.FetchAsync();
                _logger.LogInformation("Twilio connection test successful. Account: {AccountName}", account.FriendlyName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Twilio connection test failed");
                return false;
            }
        }

        #region Private Helper Methods

        private string BuildPredictionMessage(StockPrediction prediction)
        {
            var emoji = GetRecommendationEmoji(prediction.Recommendation);

            return $"📈 {emoji} {prediction.Symbol}: {prediction.Recommendation} @ ₹{prediction.CurrentPrice:F2}\n" +
                   $"Prediction: {prediction.Prediction}\n" +
                   $"Confidence: {prediction.Confidence}\n" +
                   $"Risk: {prediction.RiskLevel}";
        }

        private string GetRecommendationEmoji(string recommendation)
        {
            return recommendation?.ToLower() switch
            {
                "buy" => "🟢",
                "sell" => "🔴",
                "hold" => "🟡",
                _ => "⚪"
            };
        }

        private List<string> GetRecipients()
        {
            return _configuration.GetSection("SmsNotification:Recipients").Get<List<string>>()
                ?? new List<string>();
        }

        private string FormatPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return string.Empty;

            // Remove any non-digit characters
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());

            // Ensure it starts with '+' for international format
            if (!phoneNumber.StartsWith("+"))
            {
                // If it's a 10-digit Indian number, add +91
                if (digits.Length == 10)
                {
                    return $"+91{digits}";
                }
                return $"+{digits}";
            }

            return phoneNumber;
        }

        private string TruncateMessage(string message, int maxLength = MAX_MESSAGE_LENGTH)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
                return message ?? string.Empty;

            return message.Substring(0, maxLength - 3) + "...";
        }

        #endregion
    }

    // Optional: Add interface extension
    public interface ISmsNotificationService
    {
        Task SendSmsAsync(string to, string message);
        Task SendSmsWithRetryAsync(string to, string message, int maxRetries = 3);
        Task SendBulkSmsAsync(List<string> recipients, string message);
        Task SendPredictionAlertAsync(StockPrediction prediction);
        Task SendMarketAlertAsync(string alertType, string message);
        Task<bool> TestConnectionAsync();
    }
}