using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;

namespace StockNotificationApi.Services
{
    public class MessageFormatter : IMessageFormatter
    {
        public string FormatDailyReport(DailyPredictionReport report)
        {
            if (report == null) return "No report available.";

            var sb = new StringBuilder();
            sb.AppendLine($"<b>?? Daily Report - {report.Date:dd MMM yyyy}</b>\n");

            if (!string.IsNullOrEmpty(report.MarketSummary))
                sb.AppendLine($"<i>{report.MarketSummary}</i>\n");

            if (!string.IsNullOrEmpty(report.TopPick))
                sb.AppendLine($"?? <b>Top Pick:</b> {report.TopPick}\n");

            foreach (var stock in report.Predictions.Where(p => p != null).Take(5))
            {
                var emoji = stock.Recommendation?.ToLower() switch
                {
                    "buy" => "??",
                    "sell" => "??",
                    "hold" => "??",
                    _ => "?"
                };
                sb.AppendLine($"{emoji} <b>{stock.Symbol}</b>: {stock.Recommendation ?? "Hold"} at ?{stock.CurrentPrice:F2}");
            }

            return sb.ToString();
        }
    }
}