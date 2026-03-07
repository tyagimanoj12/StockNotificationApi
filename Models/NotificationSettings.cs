namespace StockNotificationApi.Models
{
    public class NotificationSettings
    {
        public string SendTime { get; set; } = "15:07";
        public string TimeZone { get; set; } = "India Standard Time";
    }

    public class StockApiSettings
    {
        public string AlphaVantageApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public List<string> IndianStocks { get; set; } = new();
    }

    public class GeminiAISettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiUrl { get; set; } = string.Empty;
    }

    public class EmailSettings
    {
        public string SmtpServer { get; set; } = string.Empty;
        public int SmtpPort { get; set; }
        public string SenderEmail { get; set; } = string.Empty;
        public string SenderPassword { get; set; } = string.Empty;
        public bool EnableSsl { get; set; }
        public List<string> RecipientEmails { get; set; } = new();
    }
}