// First, add Twilio package
// dotnet add package Twilio

// Services/SmsNotificationService.cs
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace StockNotificationApi.Services
{
    public class TwilioSmsService : ISmsNotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<TwilioSmsService> _logger;

        public TwilioSmsService(IConfiguration configuration, ILogger<TwilioSmsService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var accountSid = configuration["Twilio:AccountSid"];
            var authToken = configuration["Twilio:AuthToken"];

            if (!string.IsNullOrEmpty(accountSid) && !string.IsNullOrEmpty(authToken))
            {
                TwilioClient.Init(accountSid, authToken);
            }
        }

        public async Task SendSmsAsync(string to, string message)
        {
            try
            {
                var from = _configuration["Twilio:FromNumber"];

                if (string.IsNullOrEmpty(from))
                {
                    _logger.LogWarning("Twilio from number not configured");
                    return;
                }

                var messageResource = await MessageResource.CreateAsync(
                    body: message,
                    from: new PhoneNumber(from),
                    to: new PhoneNumber(to)
                );

                _logger.LogInformation("SMS sent to {To}, SID: {Sid}", to, messageResource.Sid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SMS to {To}", to);
            }
        }

        public async Task SendPredictionAlertAsync(StockPrediction prediction)
        {
            var message = $"📈 {prediction.Symbol}: {prediction.Recommendation} @ ₹{prediction.CurrentPrice}\n" +
                         $"Prediction: {prediction.Prediction}\n" +
                         $"Confidence: {prediction.Confidence}";

            var recipients = _configuration.GetSection("SmsNotification:Recipients").Get<List<string>>();

            if (recipients != null)
            {
                foreach (var recipient in recipients)
                {
                    await SendSmsAsync(recipient, message);
                }
            }
        }
    }
}