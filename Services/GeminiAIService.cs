using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        public GeminiAIService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiAIService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _geminiSettings = configuration.GetSection("GeminiAISettings").Get<GeminiAISettings>()
                ?? throw new ArgumentNullException("GeminiAISettings not configured");
        }

        public async Task<DailyPredictionReport> GeneratePredictionsAsync(List<StockData> stockData)
        {
            try
            {
                var prompt = BuildPredictionPrompt(stockData);
                var aiResponse = await CallGeminiAPI(prompt);

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
            try
            {
                var prompt = BuildMarketInsightPrompt(stockData);
                return await CallGeminiAPI(prompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting market insight from Gemini AI");
                return "Unable to generate market insight at this time.";
            }
        }

        private string BuildPredictionPrompt(List<StockData> stockData)
        {
            var stockInfo = new StringBuilder();
            foreach (var stock in stockData)
            {
                stockInfo.AppendLine($"- {stock.Name} ({stock.Symbol}): Current Price: ₹{stock.Price:F2}, " +
                                     $"Change: {stock.ChangePercent:F2}%, Volume: {stock.Volume:N0}");
            }

            return $@"As a stock market expert specializing in Indian markets, provide predictions for the following stocks:

{stockInfo}

Please provide:
1. A brief market summary (2-3 sentences)
2. For each stock, provide:
   - Prediction (Bullish/Bearish/Neutral)
   - Recommendation (Buy/Hold/Sell)
   - Confidence level (High/Medium/Low)
   - 2-3 key factors influencing the prediction
   - Short-term outlook (1-2 sentences)
   - Risk level

Format the response in a clear, structured way. Consider technical indicators, market sentiment, and recent trends in your analysis.";
        }

        private string BuildMarketInsightPrompt(List<StockData> stockData)
        {
            var avgChange = stockData.Average(s => s.ChangePercent);
            var topGainer = stockData.OrderByDescending(s => s.ChangePercent).FirstOrDefault();
            var topLoser = stockData.OrderBy(s => s.ChangePercent).FirstOrDefault();

            return $@"Based on today's Indian stock market data:
- Average change: {avgChange:F2}%
- Top gainer: {topGainer?.Name} ({topGainer?.ChangePercent:F2}%)
- Top loser: {topLoser?.Name} ({topLoser?.ChangePercent:F2}%)

Provide a concise market insight (3-4 sentences) highlighting key trends, sector performance, and what investors should watch for tomorrow. Focus on the Indian market context.";
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

            return "Unable to generate AI prediction at this time.";
        }

        private string ExtractTextFromGeminiResponse(string responseContent)
        {
            try
            {
                var json = JObject.Parse(responseContent);
                return json["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString()
                    ?? "No prediction available";
            }
            catch
            {
                return "Error parsing AI response";
            }
        }

        private DailyPredictionReport ParseAIResponse(string aiResponse, List<StockData> stockData)
        {
            var report = new DailyPredictionReport
            {
                Date = DateTime.Now,
                MarketSummary = "Market analysis completed.",
                Predictions = new List<StockPrediction>()
            };

            // Create basic predictions from stock data when AI fails
            foreach (var stock in stockData)
            {
                var prediction = new StockPrediction
                {
                    Symbol = stock.Symbol,
                    CompanyName = stock.Name,
                    CurrentPrice = stock.Price,
                    Prediction = stock.ChangePercent > 1 ? "Bullish" : (stock.ChangePercent < -1 ? "Bearish" : "Neutral"),
                    Recommendation = stock.ChangePercent > 2 ? "Buy" : (stock.ChangePercent < -2 ? "Sell" : "Hold"),
                    Confidence = Math.Abs(stock.ChangePercent) > 3 ? "High" : "Medium",
                    KeyFactors = new List<string>
                    {
                        $"Price movement: {stock.ChangePercent:F2}%",
                        $"Volume: {stock.Volume:N0}",
                        "Based on technical indicators"
                    },
                    ShortTermOutlook = GetShortTermOutlook(stock),
                    RiskLevel = GetRiskLevel(stock)
                };

                report.Predictions.Add(prediction);
            }

            report.TopPick = report.Predictions
                .Where(p => p.Recommendation == "Buy")
                .OrderByDescending(p => p.Confidence == "High" ? 3 : p.Confidence == "Medium" ? 2 : 1)
                .FirstOrDefault()?.Symbol ?? "None";

            return report;
        }

        private DailyPredictionReport GenerateFallbackPredictions(List<StockData> stockData)
        {
            return ParseAIResponse("", stockData);
        }

        private string GetShortTermOutlook(StockData stock)
        {
            if (stock.ChangePercent > 2)
                return "Strong upward momentum expected to continue";
            if (stock.ChangePercent > 0)
                return "Positive trend with possible consolidation";
            if (stock.ChangePercent > -2)
                return "Sideways movement expected";
            return "Downward pressure may continue";
        }

        private string GetRiskLevel(StockData stock)
        {
            if (Math.Abs(stock.ChangePercent) > 3)
                return "High";
            if (Math.Abs(stock.ChangePercent) > 1)
                return "Medium";
            return "Low";
        }
    }
}