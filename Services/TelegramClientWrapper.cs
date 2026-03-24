using Microsoft.Extensions.Logging;
using Polly;
using StockNotificationApi.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace StockNotificationApi.Services
{
    public interface ITelegramClientWrapper
    {
        Task StartAsync(CancellationToken token);
        Task StopAsync();
        Task SendTextAsync(long chatId, string text, ParseMode parseMode = ParseMode.Html, CancellationToken ct = default);
        Task SendTypingAsync(long chatId, CancellationToken ct = default);
        Task SendMessageWithKeyboardAsync(long chatId, string message, InlineKeyboardMarkup? keyboard = null, ParseMode parseMode = ParseMode.Html, CancellationToken ct = default);
        Task BroadcastToAllAsync(IEnumerable<long> chatIds, string message, int delayMs = 100, CancellationToken ct = default);
        TelegramBotClient Client { get; }

        // Formatting helper methods (moved from TelegramBotService)
        string FormatTopStocksMessage(List<StockData> stocks);
        string FormatVolume(long volume);
        string FormatGainersMessage(List<StockData> gainers);
        string FormatLosersMessage(List<StockData> losers);
        string FormatStockPriceMessage(StockData stock);
        List<string> SplitMessage(string message, int maxLength);
        string TruncateText(string text, int maxLength);

        // Emoji helper methods
        string GetMarketPhaseEmoji(string? phase);
        string GetSentimentEmoji(string? sentiment);
        string GetConfidenceEmoji(string? confidence);
        string GetTechnicalEmoji(string? value);
        string GetVolumeEmoji(string? volume);
        string GetNewsImpactEmoji(string? impact);
    }

    public class TelegramClientWrapper : ITelegramClientWrapper, IDisposable
    {
        private readonly ILogger<TelegramClientWrapper> _logger;
        private readonly TelegramBotClient _client;
        private readonly AsyncPolicy _retryPolicy;

        public TelegramClientWrapper(ILogger<TelegramClientWrapper> logger, string token)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Telegram token is required", nameof(token));

            _client = new TelegramBotClient(token);
            _retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) },
                    (ex, ts) => _logger.LogWarning(ex, "Telegram retry in {Delay}", ts));
        }

        public TelegramBotClient Client => _client;

        public Task StartAsync(CancellationToken token) => Task.CompletedTask;

        public async Task SendTextAsync(long chatId, string text, ParseMode parseMode = ParseMode.Html, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(text)) return;

            await _retryPolicy.ExecuteAsync(async () =>
            {
                await _client.SendMessage(chatId, text, parseMode: parseMode, cancellationToken: ct);
            });
        }

        public async Task SendTypingAsync(long chatId, CancellationToken ct = default)
        {
            try
            {
                await _client.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SendTyping failed");
            }
        }

        public async Task SendMessageWithKeyboardAsync(long chatId, string message, InlineKeyboardMarkup? keyboard = null, ParseMode parseMode = ParseMode.Html, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(message)) return;

            await _retryPolicy.ExecuteAsync(async () =>
            {
                if (keyboard != null)
                {
                    await _client.SendMessage(chatId, message, parseMode: parseMode, replyMarkup: keyboard, cancellationToken: ct);
                }
                else
                {
                    await _client.SendMessage(chatId, message, parseMode: parseMode, cancellationToken: ct);
                }
            });
        }

        public async Task BroadcastToAllAsync(IEnumerable<long> chatIds, string message, int delayMs = 100, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(message)) return;

            foreach (var chatId in chatIds)
            {
                try
                {
                    await SendTextAsync(chatId, message, ct: ct);
                    await Task.Delay(delayMs, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to broadcast to {ChatId}", chatId);
                }
            }
        }

        #region Formatting Helper Methods

        public string FormatTopStocksMessage(List<StockData> stocks)
        {
            if (stocks == null || !stocks.Any()) return "No stocks data available.";

            var topStocks = stocks
                .Where(s => s != null)
                .OrderByDescending(s => Math.Abs(s.ChangePercent))
                .Take(10)
                .ToList();

            var message = new StringBuilder(2048);
            message.AppendLine("<b>🏆 Top 10 Active Stocks</b>\n");

            foreach (var stock in topStocks)
            {
                var emoji = stock.ChangePercent > 0 ? "🟢" : "🔴";
                var arrow = stock.ChangePercent > 0 ? "▲" : "▼";
                message.AppendLine($"{emoji} <b>{stock.Symbol}</b>: ₹{stock.Price:F2} {arrow} {Math.Abs(stock.ChangePercent):F2}%");
            }

            message.AppendLine($"\n<i>Data as of {DateTime.Now:dd MMM yyyy HH:mm}</i>");
            return message.ToString();
        }

        public string FormatGainersMessage(List<StockData> gainers)
        {
            if (gainers == null || !gainers.Any()) return "📊 No gainers at the moment.";

            var message = new StringBuilder(1024);
            message.AppendLine("<b>📈 Top 5 Gainers Today</b>\n");

            for (int i = 0; i < gainers.Count; i++)
            {
                var stock = gainers[i];
                var medal = i == 0 ? "🥇" : i == 1 ? "🥈" : i == 2 ? "🥉" : "📈";
                message.AppendLine($"{medal} <b>{stock.Symbol}</b>");
                message.AppendLine($"   Price: ₹{stock.Price:F2} | Gain: <b>+{stock.ChangePercent:F2}%</b>");
                message.AppendLine($"   Volume: {FormatVolume(stock.Volume)}");
                message.AppendLine($"   Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}");
                message.AppendLine();
            }

            message.AppendLine($"<i>Last updated: {DateTime.Now:HH:mm:ss}</i>");
            return message.ToString();
        }

        public string FormatLosersMessage(List<StockData> losers)
        {
            if (losers == null || !losers.Any()) return "📊 No losers at the moment.";

            var message = new StringBuilder(1024);
            message.AppendLine("<b>📉 Top 5 Losers Today</b>\n");

            for (int i = 0; i < losers.Count; i++)
            {
                var stock = losers[i];
                var emoji = i == 0 ? "💀" : i == 1 ? "📉" : "🔻";
                message.AppendLine($"{emoji} <b>{stock.Symbol}</b>");
                message.AppendLine($"   Price: ₹{stock.Price:F2} | Loss: <b>{stock.ChangePercent:F2}%</b>");
                message.AppendLine($"   Volume: {FormatVolume(stock.Volume)}");
                message.AppendLine($"   Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}");
                message.AppendLine();
            }

            message.AppendLine($"<i>Last updated: {DateTime.Now:HH:mm:ss}</i>");
            return message.ToString();
        }

        public string FormatStockPriceMessage(StockData stock)
        {
            if (stock == null) return "❌ No data available.";

            var emoji = stock.ChangePercent > 0 ? "🟢" : "🔴";
            return $@"{emoji} <b>{stock.Symbol} - {stock.Name}</b>

Price: ₹{stock.Price:F2}
Change: {(stock.ChangePercent > 0 ? "+" : "")}{stock.ChangePercent:F2}%
Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}
Volume: {FormatVolume(stock.Volume)}";
        }

        public string FormatVolume(long volume)
        {
            if (volume >= 1_000_000_000)
                return $"{(volume / 1_000_000_000.0):F2}B";
            if (volume >= 1_000_000)
                return $"{(volume / 1_000_000.0):F2}M";
            if (volume >= 1_000)
                return $"{(volume / 1_000.0):F2}K";
            return volume.ToString("N0");
        }

        public List<string> SplitMessage(string message, int maxLength)
        {
            var parts = new List<string>();
            for (int i = 0; i < message.Length; i += maxLength)
            {
                parts.Add(message.Substring(i, Math.Min(maxLength, message.Length - i)));
            }
            return parts;
        }

        public string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text ?? string.Empty;
            return text.Substring(0, maxLength - 3) + "...";
        }

        #endregion

        #region Emoji Helper Methods

        public string GetMarketPhaseEmoji(string? phase)
        {
            if (string.IsNullOrEmpty(phase)) return "⚪";

            return phase.ToLower() switch
            {
                string p when p.Contains("bull") => "🟢",
                string p when p.Contains("bear") => "🔴",
                _ => "⚪"
            };
        }

        public string GetSentimentEmoji(string? sentiment)
        {
            if (string.IsNullOrEmpty(sentiment)) return "⚪";

            return sentiment.ToLower() switch
            {
                string s when s.Contains("bull") => "🟢",
                string s when s.Contains("bear") => "🔴",
                _ => "⚪"
            };
        }

        public string GetConfidenceEmoji(string? confidence)
        {
            if (int.TryParse(confidence, out int conf))
            {
                return conf >= 70 ? "🟢" : conf >= 50 ? "🟡" : "🔴";
            }
            return "⚪";
        }

        public string GetTechnicalEmoji(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "⚪";

            return value.ToLower() switch
            {
                string v when v.Contains("bull") => "🟢",
                string v when v.Contains("bear") => "🔴",
                _ => "⚪"
            };
        }

        public string GetVolumeEmoji(string? volume)
        {
            if (string.IsNullOrEmpty(volume)) return "⚪";

            return volume.ToLower() switch
            {
                "strong accumulation" => "🟢",
                "strong distribution" => "🔴",
                "average volume" => "⚪",
                _ => "⚪"
            };
        }

        public string GetNewsImpactEmoji(string? impact)
        {
            if (string.IsNullOrEmpty(impact)) return "⚪";

            return impact.ToLower() switch
            {
                string i when i.Contains("positive") || i.Contains("🟢") => "🟢",
                string i when i.Contains("negative") || i.Contains("🔴") => "🔴",
                string i when i.Contains("medium") || i.Contains("🟡") => "🟡",
                _ => "⚪"
            };
        }

        #endregion

        public void Dispose()
        {
            try { (_client as IDisposable)?.Dispose(); } catch { }
        }

        public Task StopAsync() { return Task.CompletedTask; }
    }
}