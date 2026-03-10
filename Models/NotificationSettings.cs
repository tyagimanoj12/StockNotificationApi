using System.ComponentModel.DataAnnotations;

namespace StockNotificationApi.Models
{
    /// <summary>
    /// Settings for notification scheduling
    /// </summary>
    public class NotificationSettings
    {
        private string _sendTime = "09:00";

        /// <summary>
        /// Time to send daily notifications in HH:mm format (24-hour)
        /// </summary>
        [Required]
        [RegularExpression(@"^([0-1]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "SendTime must be in HH:mm format (24-hour)")]
        public string SendTime
        {
            get => _sendTime;
            set => _sendTime = value ?? "09:00";
        }

        /// <summary>
        /// Time zone for scheduling (default: India Standard Time)
        /// </summary>
        public string TimeZone { get; set; } = "India Standard Time";

        /// <summary>
        /// Gets the parsed TimeSpan from SendTime
        /// </summary>
        public TimeSpan GetSendTimeAsTimeSpan()
        {
            if (TimeSpan.TryParse(SendTime, out var time))
            {
                return time;
            }
            return TimeSpan.FromHours(9); // Default to 9 AM
        }

        /// <summary>
        /// Gets the TimeZoneInfo object for the configured time zone
        /// </summary>
        public TimeZoneInfo GetTimeZoneInfo()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            }
            catch
            {
                return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
        }
    }

    /// <summary>
    /// Settings for stock API configuration
    /// </summary>
    public class StockApiSettings
    {
        private const string DEFAULT_BASE_URL = "https://www.alphavantage.co/query";

        /// <summary>
        /// Alpha Vantage API key
        /// </summary>
        [Required]
        public string AlphaVantageApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Base URL for Alpha Vantage API
        /// </summary>
        [Url]
        public string BaseUrl { get; set; } = DEFAULT_BASE_URL;

        /// <summary>
        /// List of Indian stock symbols to track
        /// </summary>
        [MinLength(1, ErrorMessage = "At least one stock symbol is required")]
        public List<string> IndianStocks { get; set; } = new();

        /// <summary>
        /// Validates that the API key is configured
        /// </summary>
        public bool IsValid => !string.IsNullOrEmpty(AlphaVantageApiKey);
    }

    /// <summary>
    /// Settings for Google Gemini AI configuration
    /// </summary>
    public class GeminiAISettings
    {
        private const string DEFAULT_API_URL = "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent";

        /// <summary>
        /// Google Gemini API key
        /// </summary>
        [Required]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gemini API endpoint URL
        /// </summary>
        [Url]
        public string ApiUrl { get; set; } = DEFAULT_API_URL;

        /// <summary>
        /// Validates that the API key is configured
        /// </summary>
        public bool IsValid => !string.IsNullOrEmpty(ApiKey);
    }

    /// <summary>
    /// Settings for email notification configuration
    /// </summary>
    public class EmailSettings
    {
        private const int DEFAULT_SMTP_PORT = 587;
        private const string DEFAULT_SMTP_SERVER = "smtp.gmail.com";

        /// <summary>
        /// SMTP server hostname
        /// </summary>
        [Required]
        public string SmtpServer { get; set; } = DEFAULT_SMTP_SERVER;

        /// <summary>
        /// SMTP server port (default: 587 for TLS)
        /// </summary>
        [Range(1, 65535)]
        public int SmtpPort { get; set; } = DEFAULT_SMTP_PORT;

        /// <summary>
        /// Email address used to send notifications
        /// </summary>
        [Required]
        [EmailAddress]
        public string SenderEmail { get; set; } = string.Empty;

        /// <summary>
        /// Password or app-specific password for the sender email
        /// </summary>
        [Required]
        public string SenderPassword { get; set; } = string.Empty;

        /// <summary>
        /// Whether to enable SSL/TLS (default: true)
        /// </summary>
        public bool EnableSsl { get; set; } = true;

        /// <summary>
        /// List of recipient email addresses
        /// </summary>
        [MinLength(1, ErrorMessage = "At least one recipient email is required")]
        public List<string> RecipientEmails { get; set; } = new();

        /// <summary>
        /// Validates that all required email settings are configured
        /// </summary>
        public bool IsValid =>
            !string.IsNullOrEmpty(SmtpServer) &&
            SmtpPort > 0 &&
            !string.IsNullOrEmpty(SenderEmail) &&
            !string.IsNullOrEmpty(SenderPassword) &&
            RecipientEmails?.Any() == true;

        /// <summary>
        /// Gets the appropriate SecureSocketOptions based on configuration
        /// </summary>
        public MailKit.Security.SecureSocketOptions GetSecureSocketOptions()
        {
            if (SmtpPort == 465)
                return MailKit.Security.SecureSocketOptions.SslOnConnect;

            if (EnableSsl || SmtpPort == 587)
                return MailKit.Security.SecureSocketOptions.StartTls;

            return MailKit.Security.SecureSocketOptions.Auto;
        }
    }
}