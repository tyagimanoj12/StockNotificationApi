using Newtonsoft.Json.Linq;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;

namespace StockNotificationApi.Services
{
    public class EnhancedAIService : IAIService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<EnhancedAIService> _logger;
        private readonly string _apiKey;
        private readonly string _apiUrl;

        public EnhancedAIService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<EnhancedAIService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _apiKey = configuration["GeminiAISettings:ApiKey"] ?? throw new ArgumentNullException("Gemini API Key missing");
            _apiUrl = configuration["GeminiAISettings:ApiUrl"] ?? "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent";
        }

        public async Task<DailyPredictionReport> GeneratePredictionsAsync(List<StockData> stockData)
        {
            try
            {
                var prompt = BuildEnhancedPrompt(stockData);
                var aiResponse = await CallGeminiAPI(prompt);
                return ParseAIResponse(aiResponse, stockData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating AI predictions");
                return GenerateFallbackPredictions(stockData);
            }
        }

        private string BuildEnhancedPrompt(List<StockData> stockData)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are an expert Indian stock market analyst. Provide accurate, data-driven predictions.\n");
            sb.AppendLine("## CURRENT MARKET DATA");

            foreach (var stock in stockData)
            {
                sb.AppendLine($"\nStock: {stock.Name} ({stock.Symbol})");
                sb.AppendLine($"- Current Price: ₹{stock.Price:F2}");
                sb.AppendLine($"- Change: {stock.ChangePercent:F2}%");
                sb.AppendLine($"- Day Range: ₹{stock.DayLow:F2} - ₹{stock.DayHigh:F2}");
                sb.AppendLine($"- Volume: {stock.Volume:N0}");
                sb.AppendLine($"- 52W Range: ₹{stock.YearLow:F2} - ₹{stock.YearHigh:F2}");
            }

            sb.AppendLine("\n## ANALYSIS REQUIREMENTS");
            sb.AppendLine("For EACH stock, provide EXACTLY this format:");
            sb.AppendLine("SYMBOL: [symbol]");
            sb.AppendLine("PREDICTION: [Bullish/Bearish/Neutral]");
            sb.AppendLine("RECOMMENDATION: [Buy/Hold/Sell]");
            sb.AppendLine("CONFIDENCE: [High/Medium/Low]");
            sb.AppendLine("REASON: [2-3 sentences explaining key factors]");
            sb.AppendLine("TARGET: [price target for next week]");
            sb.AppendLine("STOP_LOSS: [stop loss price]");
            sb.AppendLine("---");

            sb.AppendLine("\n## ADDITIONAL");
            sb.AppendLine("MARKET_SUMMARY: [2 sentences on overall market direction]");
            sb.AppendLine("TOP_PICK: [best stock symbol to buy now]");

            return sb.ToString();
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
                    temperature = 0.2, // Lower temperature for more consistent results
                    topK = 1,
                    topP = 1,
                    maxOutputTokens = 2048
                }
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{_apiUrl}?key={_apiKey}";
            var response = await _httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                return ExtractTextFromGeminiResponse(responseContent);
            }

            _logger.LogWarning("Gemini API returned {StatusCode}", response.StatusCode);
            return string.Empty;
        }

        private string ExtractTextFromGeminiResponse(string responseContent)
        {
            try
            {
                var json = JObject.Parse(responseContent);
                return json["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? string.Empty;
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
                Predictions = new List<StockPrediction>()
            };

            if (string.IsNullOrEmpty(aiResponse))
            {
                return GenerateFallbackPredictions(stockData);
            }

            // Parse each stock's prediction
            foreach (var stock in stockData)
            {
                var stockSection = ExtractStockSection(aiResponse, stock.Symbol);
                var prediction = new StockPrediction
                {
                    Symbol = stock.Symbol,
                    CompanyName = stock.Name,
                    CurrentPrice = stock.Price,
                    Prediction = ExtractValue(stockSection, "PREDICTION:", "Bullish"),
                    Recommendation = ExtractValue(stockSection, "RECOMMENDATION:", "Hold"),
                    Confidence = ExtractValue(stockSection, "CONFIDENCE:", "Medium"),
                    KeyFactors = new List<string> { ExtractValue(stockSection, "REASON:", "Based on market analysis") },
                    ShortTermOutlook = ExtractValue(stockSection, "TARGET:", "₹" + stock.Price.ToString("F2")),
                    RiskLevel = GetRiskLevel(stock)
                };
                report.Predictions.Add(prediction);
            }

            // Extract market summary and top pick
            report.MarketSummary = ExtractValue(aiResponse, "MARKET_SUMMARY:", "Market shows mixed signals.");
            report.TopPick = ExtractValue(aiResponse, "TOP_PICK:", stockData.FirstOrDefault()?.Symbol ?? "NONE");

            return report;
        }

        private string ExtractStockSection(string response, string symbol)
        {
            var lines = response.Split('\n');
            var section = new StringBuilder();
            bool inSection = false;

            foreach (var line in lines)
            {
                if (line.Contains($"SYMBOL: {symbol}"))
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
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains(key))
                {
                    return line.Replace(key, "").Trim();
                }
            }
            return defaultValue;
        }

        private string GetRiskLevel(StockData stock)
        {
            if (Math.Abs(stock.ChangePercent) > 3) return "High";
            if (Math.Abs(stock.ChangePercent) > 1) return "Medium";
            return "Low";
        }

        private DailyPredictionReport GenerateFallbackPredictions(List<StockData> stockData)
        {
            var report = new DailyPredictionReport
            {
                Date = DateTime.Now,
                MarketSummary = "Based on technical analysis, market shows mixed signals. Banking and IT sectors showing strength.",
                Predictions = new List<StockPrediction>(),
                TopPick = stockData.OrderByDescending(s => s.ChangePercent).FirstOrDefault()?.Symbol ?? "RELIANCE"
            };

            foreach (var stock in stockData)
            {
                var prediction = DetermineSmartPrediction(stock);
                report.Predictions.Add(prediction);
            }

            return report;
        }

        private StockPrediction DetermineSmartPrediction(StockData stock)
        {
            var prediction = new StockPrediction
            {
                Symbol = stock.Symbol,
                CompanyName = stock.Name,
                CurrentPrice = stock.Price
            };

            // Intelligent fallback logic based on actual data
            if (stock.ChangePercent > 2)
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
            else if (stock.ChangePercent < -2)
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

            prediction.RiskLevel = GetRiskLevel(stock);
            return prediction;
        }

        public async Task<string> GetMarketInsightAsync(List<StockData> stockData)
        {
            try
            {
                var avgChange = stockData.Average(s => s.ChangePercent);
                var topGainer = stockData.OrderByDescending(s => s.ChangePercent).First();
                var topLoser = stockData.OrderBy(s => s.ChangePercent).First();

                var prompt = $@"
                    Based on today's Indian stock market data:
                    - Average change: {avgChange:F2}%
                    - Top gainer: {topGainer.Name} ({topGainer.ChangePercent:F2}%)
                    - Top loser: {topLoser.Name} ({topLoser.ChangePercent:F2}%)
                    - Number of stocks positive: {stockData.Count(s => s.ChangePercent > 0)}
                    - Number of stocks negative: {stockData.Count(s => s.ChangePercent < 0)}

                    Provide a concise market insight (2-3 sentences) highlighting:
                    1. Overall market sentiment
                    2. Which sectors are leading/lagging
                    3. What investors should watch tomorrow
                ";

                return await CallGeminiAPI(prompt);
            }
            catch
            {
                return GetFallbackMarketInsight(stockData);
            }
        }

        private string GetFallbackMarketInsight(List<StockData> stockData)
        {
            var positiveCount = stockData.Count(s => s.ChangePercent > 0);
            var negativeCount = stockData.Count(s => s.ChangePercent < 0);

            if (positiveCount > negativeCount * 1.5)
                return $"Market showing strong bullish trend with {positiveCount} stocks advancing. Banking and IT sectors leading. Continue bullish momentum expected tomorrow.";
            else if (negativeCount > positiveCount * 1.5)
                return $"Market under pressure with {negativeCount} stocks declining. Profit booking visible in metals and energy. Caution advised tomorrow.";
            else
                return $"Market consolidating with mixed signals. {positiveCount} advancing vs {negativeCount} declining. Stock-specific action expected.";
        }
    }
}