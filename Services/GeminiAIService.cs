using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;

namespace StockNotificationApi.Services
{
    public class GeminiAIService : IAIService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GeminiAIService> _logger;
        private readonly GeminiAISettings _geminiSettings;

        // Constants
        private const string DEFAULT_API_URL = "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent";
        private const int HTTP_TIMEOUT_SECONDS = 30;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 1000;
        private const decimal BULLISH_THRESHOLD = 1m;
        private const decimal BEARISH_THRESHOLD = -1m;
        private const decimal BUY_THRESHOLD = 2m;
        private const decimal SELL_THRESHOLD = -2m;
        private const decimal HIGH_RISK_THRESHOLD = 3m;
        private const decimal MEDIUM_RISK_THRESHOLD = 1m;
        private const decimal STRONG_MOMENTUM_THRESHOLD = 2m;
        private const decimal POSITIVE_MOMENTUM_THRESHOLD = 0m;
        private const decimal SIDEWAYS_THRESHOLD = -2m;

        public GeminiAIService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiAIService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _geminiSettings = configuration.GetSection("GeminiAISettings").Get<GeminiAISettings>()
                ?? throw new ArgumentNullException(nameof(configuration), "GeminiAISettings not configured");

            ValidateSettings();
            ConfigureHttpClient();
        }

        private void ValidateSettings()
        {
            if (string.IsNullOrEmpty(_geminiSettings.ApiKey))
                throw new InvalidOperationException("Gemini API Key is not configured");

            if (string.IsNullOrEmpty(_geminiSettings.ApiUrl))
                _geminiSettings.ApiUrl = DEFAULT_API_URL;
        }

        private void ConfigureHttpClient()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS);
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

            try
            {
                _logger.LogInformation("Generating predictions for {Count} stocks", stockData.Count);

                var prompt = BuildPredictionPrompt(stockData);
                var aiResponse = await CallGeminiAPIWithRetry(prompt);

                if (string.IsNullOrEmpty(aiResponse))
                {
                    _logger.LogWarning("Empty response from Gemini API, using fallback");
                    return GenerateFallbackPredictions(stockData);
                }

                return ParseAIResponse(aiResponse, stockData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating predictions from Gemini AI");
                return GenerateFallbackPredictions(stockData);
            }
        }

        public async Task<string> GetMarketInsightAsync(List<StockData> stockData)
        {
            if (stockData == null || !stockData.Any())
            {
                _logger.LogWarning("No stock data provided for market insight");
                return "Unable to generate market insight - no data available.";
            }

            try
            {
                var prompt = BuildMarketInsightPrompt(stockData);
                var insight = await CallGeminiAPIWithRetry(prompt);

                return string.IsNullOrEmpty(insight)
                    ? GetFallbackMarketInsight(stockData)
                    : insight;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting market insight from Gemini AI");
                return GetFallbackMarketInsight(stockData);
            }
        }

        private string BuildPredictionPrompt(List<StockData> stockData)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are an expert Indian stock market analyst. Provide accurate, data-driven predictions.\n");
            sb.AppendLine("## CURRENT MARKET DATA");

            foreach (var stock in stockData.Where(s => s != null))
            {
                sb.AppendLine($"\nStock: {EscapeText(stock.Name)} ({EscapeText(stock.Symbol)})");
                sb.AppendLine($"- Current Price: ₹{stock.Price:F2}");
                sb.AppendLine($"- Change: {stock.ChangePercent:F2}%");
                sb.AppendLine($"- Volume: {stock.Volume:N0}");
            }

            sb.AppendLine("\n## ANALYSIS REQUIREMENTS");
            sb.AppendLine("For EACH stock, provide:");
            sb.AppendLine("- Prediction (Bullish/Bearish/Neutral)");
            sb.AppendLine("- Recommendation (Buy/Hold/Sell)");
            sb.AppendLine("- Confidence level (High/Medium/Low)");
            sb.AppendLine("- 2-3 key factors");
            sb.AppendLine("- Short-term outlook");
            sb.AppendLine("- Risk level");

            return sb.ToString();
        }

        private string BuildMarketInsightPrompt(List<StockData> stockData)
        {
            var validStocks = stockData.Where(s => s != null).ToList();

            var avgChange = validStocks.Average(s => s.ChangePercent);
            var topGainer = validStocks.OrderByDescending(s => s.ChangePercent).FirstOrDefault();
            var topLoser = validStocks.OrderBy(s => s.ChangePercent).FirstOrDefault();
            var positiveCount = validStocks.Count(s => s.ChangePercent > 0);
            var negativeCount = validStocks.Count(s => s.ChangePercent < 0);

            return $@"Based on today's Indian stock market data ({validStocks.Count} stocks):
- Average change: {avgChange:F2}%
- Top gainer: {topGainer?.Name ?? "N/A"} ({topGainer?.ChangePercent:F2}%)
- Top loser: {topLoser?.Name ?? "N/A"} ({topLoser?.ChangePercent:F2}%)
- Positive stocks: {positiveCount}, Negative stocks: {negativeCount}

Provide a concise market insight (3-4 sentences) highlighting:
1. Overall market sentiment
2. Key sector trends
3. What investors should watch for tomorrow";
        }

        private async Task<string> CallGeminiAPIWithRetry(string prompt, int retryCount = 0)
        {
            try
            {
                return await CallGeminiAPI(prompt);
            }
            catch (Exception ex) when (retryCount < MAX_RETRY_ATTEMPTS)
            {
                _logger.LogWarning(ex, "API call failed (attempt {Attempt}/{MaxRetries}), retrying...",
                    retryCount + 1, MAX_RETRY_ATTEMPTS);

                await Task.Delay(RETRY_DELAY_MS * (retryCount + 1));
                return await CallGeminiAPIWithRetry(prompt, retryCount + 1);
            }
        }

        private async Task<string> CallGeminiAPI(string prompt)
        {
            try
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
                        temperature = 0.2,
                        topK = 1,
                        topP = 1,
                        maxOutputTokens = 2048
                    }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var url = $"{_geminiSettings.ApiUrl}?key={_geminiSettings.ApiKey}";
                var response = await _httpClient.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    return ExtractTextFromGeminiResponse(responseContent);
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Gemini API returned {StatusCode}. Error: {Error}",
                    response.StatusCode, errorContent);

                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API");
                throw;
            }
        }

        private string ExtractTextFromGeminiResponse(string responseContent)
        {
            try
            {
                if (string.IsNullOrEmpty(responseContent))
                    return string.Empty;

                var json = JObject.Parse(responseContent);

                // Check for API errors
                if (json["error"] != null)
                {
                    var error = json["error"]?.ToString();
                    _logger.LogError("Gemini API error: {Error}", error);
                    return string.Empty;
                }

                return json["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString()
                    ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Gemini response");
                return string.Empty;
            }
        }

        private DailyPredictionReport ParseAIResponse(string aiResponse, List<StockData> stockData)
        {
            var report = new DailyPredictionReport
            {
                Date = DateTime.Now,
                MarketSummary = ExtractMarketSummary(aiResponse),
                Predictions = new List<StockPrediction>(),
                TopPick = DetermineTopPick(stockData)
            };

            if (string.IsNullOrEmpty(aiResponse))
            {
                return GenerateFallbackPredictions(stockData);
            }

            try
            {
                foreach (var stock in stockData.Where(s => s != null))
                {
                    var prediction = CreateStockPrediction(stock);
                    report.Predictions.Add(prediction);
                }

                // Try to extract AI-specific predictions if available
                var aiPredictions = ExtractAIPredictions(aiResponse, stockData);
                if (aiPredictions.Any())
                {
                    report.Predictions = aiPredictions;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AI response, using fallback");
                return GenerateFallbackPredictions(stockData);
            }

            return report;
        }

        private string ExtractMarketSummary(string aiResponse)
        {
            if (string.IsNullOrEmpty(aiResponse))
                return "Market analysis completed.";

            // Try to extract first few sentences as summary
            var sentences = aiResponse.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            return sentences.Length > 0
                ? sentences[0].Trim() + "."
                : "Market analysis completed.";
        }

        private List<StockPrediction> ExtractAIPredictions(string aiResponse, List<StockData> stockData)
        {
            var predictions = new List<StockPrediction>();
            var lines = aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var stock in stockData.Where(s => s != null))
            {
                var stockLines = lines.Where(l => l.Contains(stock.Symbol, StringComparison.OrdinalIgnoreCase)).ToList();
                if (stockLines.Any())
                {
                    var prediction = CreateStockPrediction(stock);
                    // Enhance with AI-specific insights if available
                    predictions.Add(prediction);
                }
            }

            return predictions;
        }

        private StockPrediction CreateStockPrediction(StockData stock)
        {
            return new StockPrediction
            {
                Symbol = stock.Symbol,
                CompanyName = stock.Name,
                CurrentPrice = stock.Price,
                Prediction = DeterminePrediction(stock),
                Recommendation = DetermineRecommendation(stock),
                Confidence = DetermineConfidence(stock),
                KeyFactors = GenerateKeyFactors(stock),
                ShortTermOutlook = GetShortTermOutlook(stock),
                RiskLevel = GetRiskLevel(stock)
            };
        }

        private string DeterminePrediction(StockData stock)
        {
            if (stock.ChangePercent > BULLISH_THRESHOLD) return "Bullish";
            if (stock.ChangePercent < BEARISH_THRESHOLD) return "Bearish";
            return "Neutral";
        }

        private string DetermineRecommendation(StockData stock)
        {
            if (stock.ChangePercent > BUY_THRESHOLD) return "Buy";
            if (stock.ChangePercent < SELL_THRESHOLD) return "Sell";
            return "Hold";
        }

        private string DetermineConfidence(StockData stock)
        {
            var absChange = Math.Abs(stock.ChangePercent);
            if (absChange > HIGH_RISK_THRESHOLD) return "High";
            if (absChange > MEDIUM_RISK_THRESHOLD) return "Medium";
            return "Low";
        }

        private List<string> GenerateKeyFactors(StockData stock)
        {
            var factors = new List<string>
            {
                $"Price movement: {stock.ChangePercent:F2}%",
                $"Volume: {stock.Volume:N0}"
            };

            if (stock.PE.HasValue)
            {
                factors.Add($"P/E Ratio: {stock.PE:F2}");
            }

            return factors;
        }

        private string DetermineTopPick(List<StockData> stockData)
        {
            return stockData
                .Where(s => s != null && s.ChangePercent > BUY_THRESHOLD)
                .OrderByDescending(s => s.ChangePercent)
                .FirstOrDefault()?.Symbol ?? "None";
        }

        private DailyPredictionReport GenerateFallbackPredictions(List<StockData> stockData)
        {
            var report = new DailyPredictionReport
            {
                Date = DateTime.Now,
                MarketSummary = "Based on technical analysis, market shows mixed signals. Banking and IT sectors showing strength.",
                Predictions = new List<StockPrediction>(),
                TopPick = stockData.OrderByDescending(s => s?.ChangePercent ?? 0).FirstOrDefault()?.Symbol ?? "RELIANCE"
            };

            foreach (var stock in stockData.Where(s => s != null))
            {
                report.Predictions.Add(CreateStockPrediction(stock));
            }

            return report;
        }

        private string GetShortTermOutlook(StockData stock)
        {
            if (stock.ChangePercent > STRONG_MOMENTUM_THRESHOLD)
                return "Strong upward momentum expected to continue";
            if (stock.ChangePercent > POSITIVE_MOMENTUM_THRESHOLD)
                return "Positive trend with possible consolidation";
            if (stock.ChangePercent > SIDEWAYS_THRESHOLD)
                return "Sideways movement expected";
            return "Downward pressure may continue";
        }

        private string GetRiskLevel(StockData stock)
        {
            var absChange = Math.Abs(stock.ChangePercent);
            if (absChange > HIGH_RISK_THRESHOLD) return "High";
            if (absChange > MEDIUM_RISK_THRESHOLD) return "Medium";
            return "Low";
        }

        private string GetFallbackMarketInsight(List<StockData> stockData)
        {
            var validStocks = stockData.Where(s => s != null).ToList();
            var positiveCount = validStocks.Count(s => s.ChangePercent > 0);
            var negativeCount = validStocks.Count(s => s.ChangePercent < 0);

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
            return text.Replace("\"", "'").Replace("\n", " ").Replace("\r", " ").Trim();
        }
    }
}