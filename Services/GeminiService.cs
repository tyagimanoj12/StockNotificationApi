using Newtonsoft.Json.Linq;
using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;

namespace StockNotificationApi.Services
{
    public class GeminiService : IAIService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GeminiService> _logger;
        private readonly string _apiKey;
        private readonly string _apiUrl;

        // Use constants from Constants.cs
        private const double DEFAULT_TEMPERATURE = GeminiConstants.DEFAULT_TEMPERATURE;
        private const int DEFAULT_TOP_K = GeminiConstants.DEFAULT_TOP_K;
        private const int DEFAULT_TOP_P = GeminiConstants.DEFAULT_TOP_P;
        private const int DEFAULT_MAX_TOKENS = GeminiConstants.DEFAULT_MAX_TOKENS;
        private const int RATE_LIMIT_DELAY_MS = RateLimitConstants.RATE_LIMIT_DELAY_MS;
        private const int MAX_RETRIES = TimeoutConstants.MAX_RETRY_ATTEMPTS;

        // Thresholds
        private const decimal BULLISH_THRESHOLD = StockConstants.BULLISH_THRESHOLD;
        private const decimal BEARISH_THRESHOLD = StockConstants.BEARISH_THRESHOLD;
        private const decimal BUY_THRESHOLD = StockConstants.BUY_THRESHOLD;
        private const decimal SELL_THRESHOLD = StockConstants.SELL_THRESHOLD;
        private const decimal HIGH_RISK_THRESHOLD = StockConstants.HIGH_RISK_THRESHOLD;
        private const decimal MEDIUM_RISK_THRESHOLD = StockConstants.MEDIUM_RISK_THRESHOLD;
        private const decimal STRONG_MOMENTUM_THRESHOLD = StockConstants.STRONG_MOMENTUM_THRESHOLD;
        private const decimal SIGNIFICANT_DECLINE_THRESHOLD = StockConstants.SIGNIFICANT_DECLINE_THRESHOLD;

        public GeminiService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _apiKey = configuration["GeminiAISettings:ApiKey"]
                ?? throw new ArgumentNullException("GeminiAISettings:ApiKey", "Gemini API Key missing");

            _apiUrl = configuration["GeminiAISettings:ApiUrl"]
                ?? "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent";

            ConfigureHttpClient();
        }

        private void ConfigureHttpClient()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<DailyPredictionReport> GeneratePredictionsAsync(List<StockData> stockData)
        {
            if (stockData == null || !stockData.Any())
            {
                _logger.LogWarning("No stock data provided for prediction generation");
                return CreateEmptyReport();
            }

            _logger.LogInformation("Generating predictions for {Count} stocks", stockData.Count);

            try
            {
                // Use EnhancedAIService's better prompt
                var prompt = BuildEnhancedPrompt(stockData);
                var aiResponse = await CallGeminiAPIWithRetry(prompt);

                if (string.IsNullOrEmpty(aiResponse))
                {
                    _logger.LogWarning("Empty response from Gemini API, using fallback");
                    return GenerateFallbackPredictions(stockData);
                }

                // Use EnhancedAIService's better parsing
                return ParseAIResponse(aiResponse, stockData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating AI predictions");
                return GenerateFallbackPredictions(stockData);
            }
        }

        public async Task<string> GetMarketInsightAsync(List<StockData> stockData)
        {
            if (stockData == null || !stockData.Any())
            {
                return "No market data available for analysis.";
            }

            try
            {
                var avgChange = stockData.Average(s => s.ChangePercent);
                var topGainer = stockData.OrderByDescending(s => s.ChangePercent).FirstOrDefault();
                var topLoser = stockData.OrderBy(s => s.ChangePercent).FirstOrDefault();
                var positiveCount = stockData.Count(s => s.ChangePercent > 0);
                var negativeCount = stockData.Count(s => s.ChangePercent < 0);

                var prompt = $@"
Based on today's Indian stock market data:
- Average change: {avgChange:F2}%
- Top gainer: {topGainer?.Name ?? "N/A"} ({topGainer?.ChangePercent:F2}%)
- Top loser: {topLoser?.Name ?? "N/A"} ({topLoser?.ChangePercent:F2}%)
- Number of stocks positive: {positiveCount}
- Number of stocks negative: {negativeCount}

Provide a concise market insight (2-3 sentences) highlighting:
1. Overall market sentiment
2. Which sectors are leading/lagging
3. What investors should watch tomorrow";

                var insight = await CallGeminiAPIWithRetry(prompt);
                return string.IsNullOrEmpty(insight) ? GetFallbackMarketInsight(stockData) : insight;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting market insight");
                return GetFallbackMarketInsight(stockData);
            }
        }

        // From EnhancedAIService - Better prompt with more data
        private string BuildEnhancedPrompt(List<StockData> stockData)
        {
            if (stockData == null) return string.Empty;

            var sb = new StringBuilder();

            sb.AppendLine("You are an expert Indian stock market analyst with 20 years of experience.");
            sb.AppendLine("Provide accurate, data-driven predictions based on the following data.\n");

            sb.AppendLine($"## MARKET OVERVIEW");
            sb.AppendLine($"Date: {DateTime.Now:dd MMM yyyy}");
            sb.AppendLine($"Total Stocks: {stockData.Count}\n");

            sb.AppendLine("## CURRENT MARKET DATA");

            foreach (var stock in stockData.Where(s => s != null).Take(20)) // Limit to 20 stocks
            {
                sb.AppendLine($"\nStock: {EscapeText(stock.Name)} ({EscapeText(stock.Symbol)})");
                sb.AppendLine($"- Current Price: ₹{stock.Price:F2}");
                sb.AppendLine($"- Change: {stock.ChangePercent:F2}%");
                sb.AppendLine($"- Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}");
                sb.AppendLine($"- Volume: {stock.Volume:N0}");
                sb.AppendLine($"- 52W Range: ₹{stock.YearLow:F2} - ₹{stock.YearHigh:F2}");
                if (stock.PE.HasValue)
                    sb.AppendLine($"- P/E Ratio: {stock.PE:F2}");
                sb.AppendLine($"- Sector: {stock.Sector}");
            }

            sb.AppendLine("\n## ANALYSIS REQUIREMENTS");
            sb.AppendLine("For EACH stock, provide EXACTLY this format (one per line):");
            sb.AppendLine("SYMBOL: [symbol]");
            sb.AppendLine("PREDICTION: [Bullish/Bearish/Neutral]");
            sb.AppendLine("RECOMMENDATION: [Buy/Hold/Sell]");
            sb.AppendLine("CONFIDENCE: [High/Medium/Low]");
            sb.AppendLine("TARGET: [price target for next week in ₹]");
            sb.AppendLine("STOP_LOSS: [stop loss price in ₹]");
            sb.AppendLine("REASON: [2-3 sentences explaining key factors]");
            sb.AppendLine("---");

            sb.AppendLine("\n## ADDITIONAL");
            sb.AppendLine("MARKET_SUMMARY: [2 sentences on overall market direction]");
            sb.AppendLine("TOP_PICK: [best stock symbol to buy now]");

            return sb.ToString();
        }

        private async Task<string> CallGeminiAPIWithRetry(string prompt, int retryCount = 0)
        {
            try
            {
                return await CallGeminiAPI(prompt);
            }
            catch (Exception ex) when (retryCount < MAX_RETRIES)
            {
                _logger.LogWarning(ex, "API call failed (attempt {RetryCount}/{MaxRetries}), retrying...",
                    retryCount + 1, MAX_RETRIES);

                await Task.Delay(RATE_LIMIT_DELAY_MS * (retryCount + 1));
                return await CallGeminiAPIWithRetry(prompt, retryCount + 1);
            }
        }

        private async Task<string> CallGeminiAPI(string prompt)
        {
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = DEFAULT_TEMPERATURE,
                    topK = DEFAULT_TOP_K,
                    topP = DEFAULT_TOP_P,
                    maxOutputTokens = DEFAULT_MAX_TOKENS
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_apiUrl}?key={_apiKey}";
            var response = await _httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                return ExtractTextFromGeminiResponse(responseContent);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API returned {StatusCode}. Response: {Error}",
                response.StatusCode, errorContent);

            return string.Empty;
        }

        private string ExtractTextFromGeminiResponse(string responseContent)
        {
            try
            {
                var json = JObject.Parse(responseContent);

                // Check for error in response
                if (json["error"] != null)
                {
                    var error = json["error"]?.ToString();
                    _logger.LogError("Gemini API error: {Error}", error);
                    return string.Empty;
                }

                return json["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Gemini response");
                return string.Empty;
            }
        }

        // From EnhancedAIService - Better parsing
        private DailyPredictionReport ParseAIResponse(string aiResponse, List<StockData> stockData)
        {
            var report = new DailyPredictionReport
            {
                Date = DateTime.Now,
                Predictions = new List<StockPrediction>(),
                MarketSummary = "Market analysis completed.",
                TopPick = "NONE"
            };

            if (string.IsNullOrEmpty(aiResponse))
            {
                return GenerateFallbackPredictions(stockData);
            }

            try
            {
                // Parse each stock's prediction
                foreach (var stock in stockData.Where(s => s != null))
                {
                    var stockSection = ExtractStockSection(aiResponse, stock.Symbol);
                    var prediction = ParseStockPrediction(stockSection, stock);
                    report.Predictions.Add(prediction);
                }

                // Extract market summary and top pick
                report.MarketSummary = ExtractValue(aiResponse, "MARKET_SUMMARY:", "Market shows mixed signals.");
                report.TopPick = ExtractValue(aiResponse, "TOP_PICK:", stockData.FirstOrDefault()?.Symbol ?? "NONE");

                _logger.LogInformation("Successfully parsed AI response for {Count} stocks", report.Predictions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AI response, using fallback");
                return GenerateFallbackPredictions(stockData);
            }

            return report;
        }

        private StockPrediction ParseStockPrediction(string stockSection, StockData stock)
        {
            var prediction = new StockPrediction
            {
                Symbol = stock.Symbol,
                CompanyName = stock.Name,
                CurrentPrice = stock.Price,
                Prediction = ExtractValue(stockSection, "PREDICTION:", "Neutral"),
                Recommendation = ExtractValue(stockSection, "RECOMMENDATION:", "Hold"),
                Confidence = ExtractValue(stockSection, "CONFIDENCE:", "Medium"),
                KeyFactors = new List<string>(),
                ShortTermOutlook = ExtractValue(stockSection, "TARGET:", $"₹{stock.Price:F2}"),
                RiskLevel = GetRiskLevel(stock)
            };

            var reason = ExtractValue(stockSection, "REASON:", "");
            if (!string.IsNullOrEmpty(reason))
            {
                prediction.KeyFactors.Add(reason);
            }

            return prediction;
        }

        private string ExtractStockSection(string response, string symbol)
        {
            if (string.IsNullOrEmpty(response) || string.IsNullOrEmpty(symbol))
                return string.Empty;

            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var section = new StringBuilder();
            bool inSection = false;

            foreach (var line in lines)
            {
                if (line.Contains($"SYMBOL: {symbol}", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    continue;
                }

                if (inSection && line.Contains("---"))
                {
                    break;
                }

                if (inSection)
                {
                    section.AppendLine(line);
                }
            }

            return section.ToString();
        }

        private string ExtractValue(string text, string key, string defaultValue)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key))
                return defaultValue;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring(line.IndexOf(key) + key.Length).Trim();
                    return string.IsNullOrEmpty(value) ? defaultValue : value;
                }
            }
            return defaultValue;
        }

        private string GetRiskLevel(StockData stock)
        {
            if (stock == null) return "Medium";

            var changePercent = Math.Abs(stock.ChangePercent);

            if (changePercent > HIGH_RISK_THRESHOLD) return "High";
            if (changePercent > MEDIUM_RISK_THRESHOLD) return "Medium";
            return "Low";
        }

        // Fallback methods from both
        private DailyPredictionReport GenerateFallbackPredictions(List<StockData> stockData)
        {
            if (stockData == null || !stockData.Any())
                return CreateEmptyReport();

            var report = new DailyPredictionReport
            {
                Date = DateTime.Now,
                MarketSummary = "Based on technical analysis, market shows mixed signals. Banking and IT sectors showing strength.",
                Predictions = new List<StockPrediction>(),
                TopPick = stockData.OrderByDescending(s => s?.ChangePercent ?? 0)
                                   .FirstOrDefault()?.Symbol ?? "RELIANCE"
            };

            foreach (var stock in stockData.Where(s => s != null))
            {
                report.Predictions.Add(DetermineSmartPrediction(stock));
            }

            return report;
        }

        private StockPrediction DetermineSmartPrediction(StockData stock)
        {
            var prediction = new StockPrediction
            {
                Symbol = stock.Symbol,
                CompanyName = stock.Name,
                CurrentPrice = stock.Price,
                RiskLevel = GetRiskLevel(stock)
            };

            if (stock.ChangePercent > STRONG_MOMENTUM_THRESHOLD)
            {
                prediction.Prediction = "Bullish";
                prediction.Recommendation = "Buy";
                prediction.Confidence = "High";
                prediction.KeyFactors = new List<string>
                {
                    $"Strong upward momentum with {stock.ChangePercent:F2}% gain",
                    $"Trading near day high of ₹{stock.DayHigh:F2}",
                    $"Above average volume of {stock.Volume:N0}"
                };
                prediction.ShortTermOutlook = $"Expected to test ₹{stock.DayHigh * 1.02m:F2} soon";
            }
            else if (stock.ChangePercent < SIGNIFICANT_DECLINE_THRESHOLD)
            {
                prediction.Prediction = "Bearish";
                prediction.Recommendation = "Sell";
                prediction.Confidence = "Medium";
                prediction.KeyFactors = new List<string>
                {
                    $"Significant decline of {stock.ChangePercent:F2}%",
                    $"Testing support at ₹{stock.DayLow:F2}",
                    $"Higher than average selling pressure"
                };
                prediction.ShortTermOutlook = $"May test ₹{stock.YearLow:F2} if selling continues";
            }
            else
            {
                prediction.Prediction = "Neutral";
                prediction.Recommendation = "Hold";
                prediction.Confidence = "Medium";
                prediction.KeyFactors = new List<string>
                {
                    $"Consolidating in range ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}",
                    $"Volume of {stock.Volume:N0} is normal",
                    $"Awaiting clear direction"
                };
                prediction.ShortTermOutlook = "Sideways movement expected";
            }

            return prediction;
        }

        private string GetFallbackMarketInsight(List<StockData> stockData)
        {
            if (stockData == null || !stockData.Any())
                return "Market data unavailable.";

            var positiveCount = stockData.Count(s => s.ChangePercent > 0);
            var negativeCount = stockData.Count(s => s.ChangePercent < 0);

            if (positiveCount > negativeCount * 1.5)
                return $"Market showing strong bullish trend with {positiveCount} stocks advancing. Banking and IT sectors leading. Continue bullish momentum expected tomorrow.";
            else if (negativeCount > positiveCount * 1.5)
                return $"Market under pressure with {negativeCount} stocks declining. Profit booking visible in metals and energy. Caution advised tomorrow.";
            else
                return $"Market consolidating with mixed signals. {positiveCount} advancing vs {negativeCount} declining. Stock-specific action expected.";
        }

        private DailyPredictionReport CreateEmptyReport()
        {
            return new DailyPredictionReport
            {
                Date = DateTime.Now,
                MarketSummary = "No data available for analysis.",
                Predictions = new List<StockPrediction>(),
                TopPick = "NONE"
            };
        }

        private string EscapeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            return text.Replace("\"", "'")
                      .Replace("\n", " ")
                      .Replace("\r", " ")
                      .Trim();
        }
    }
}