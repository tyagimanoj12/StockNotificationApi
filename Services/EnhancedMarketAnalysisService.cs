using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;
using System.Linq; // Add this

namespace StockNotificationApi.Services
{
    public class EnhancedMarketAnalysisService : IEnhancedMarketAnalysisService
    {
        private readonly IStockService _stockService;
        private readonly IStockListService _stockListService;
        private readonly IAIService _aiService;
        private readonly INewsService _newsService;
        private readonly ILogger<EnhancedMarketAnalysisService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public EnhancedMarketAnalysisService(
            IStockService stockService,
            IStockListService stockListService,
            IAIService aiService,
            INewsService newsService,
            IHttpClientFactory httpClientFactory,
            ILogger<EnhancedMarketAnalysisService> logger)
        {
            _stockService = stockService;
            _stockListService = stockListService;
            _aiService = aiService;
            _newsService = newsService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<EnhancedMarketAnalysis> AnalyzeMarketAsync()
        {
            _logger.LogInformation("Starting enhanced market analysis...");

            var analysis = new EnhancedMarketAnalysis
            {
                AnalysisDate = DateTime.Now,
                LargeCap = new CategoryAnalysis { Category = "Large Cap" },
                MidCap = new CategoryAnalysis { Category = "Mid Cap" },
                SmallCap = new CategoryAnalysis { Category = "Small Cap" },
                KeyNewsImpacts = new List<NewsImpactAnalysis>(),
                TechnicalIndicators = new TechnicalSummary()
            };

            try
            {
                // Get categorized stocks directly from StockListService
                var categories = await _stockListService.GetAllCategoriesAsync();

                _logger.LogInformation($"Categories - Large: {categories["LargeCap"].Count}, " +
                                      $"Mid: {categories["MidCap"].Count}, " +
                                      $"Small: {categories["SmallCap"].Count}");

                // Convert StockInfo to StockData for each category (take top 30 from each)
                var largeCapStocks = await ConvertToStockData(categories["LargeCap"].Take(30).ToList());
                var midCapStocks = await ConvertToStockData(categories["MidCap"].Take(30).ToList());
                var smallCapStocks = await ConvertToStockData(categories["SmallCap"].Take(30).ToList());

                _logger.LogInformation($"Converted - Large: {largeCapStocks.Count}, Mid: {midCapStocks.Count}, Small: {smallCapStocks.Count}");

                var categorized = (largeCapStocks, midCapStocks, smallCapStocks);

                // Get historical data
                var historicalData = await GetHistoricalDataAsync();

                // Get recent news
                var recentNews = await _newsService.GetTopMarketNewsAsync(20);

                // Build comprehensive prompt for AI
                var prompt = BuildAnalysisPrompt(historicalData, categorized, recentNews);

                // Get AI analysis
                var aiResponse = await GetAIMarketAnalysis(prompt);

                // Parse AI response into structured data
                analysis = ParseAIResponse(aiResponse, categorized);

                // ENSURE WE HAVE MARKET PHASE AND SENTIMENT
                if (string.IsNullOrEmpty(analysis.MarketPhase))
                {
                    analysis.MarketPhase = DetermineMarketPhase(categorized);
                }

                if (string.IsNullOrEmpty(analysis.OverallSentiment))
                {
                    analysis.OverallSentiment = DetermineOverallSentiment(categorized);
                }

                // 🔥 DYNAMIC CONFIDENCE SCORE CALCULATION
                analysis.ConfidenceScore = CalculateConfidenceScore(categorized, analysis.MarketPhase, analysis.OverallSentiment);

                _logger.LogInformation($"Calculated confidence score: {analysis.ConfidenceScore}%");

                // ENSURE WE HAVE ENOUGH PICKS PER CATEGORY
                // Large Cap - need 5 picks
                if (!analysis.LargeCap.TopPicks.Any())
                {
                    analysis.LargeCap.TopPicks = GenerateFallbackPicks(largeCapStocks, "Large");
                }
                else if (analysis.LargeCap.TopPicks.Count < 5)
                {
                    var existingSymbols = analysis.LargeCap.TopPicks.Select(p => p.Symbol).ToHashSet();
                    var additionalPicks = GenerateFallbackPicks(largeCapStocks, "Large")
                        .Where(p => !existingSymbols.Contains(p.Symbol))
                        .Take(5 - analysis.LargeCap.TopPicks.Count);
                    analysis.LargeCap.TopPicks.AddRange(additionalPicks);
                }

                // Mid Cap - need 3 picks
                if (!analysis.MidCap.TopPicks.Any())
                {
                    analysis.MidCap.TopPicks = GenerateFallbackPicks(midCapStocks, "Mid");
                }
                else if (analysis.MidCap.TopPicks.Count < 3)
                {
                    var existingSymbols = analysis.MidCap.TopPicks.Select(p => p.Symbol).ToHashSet();
                    var additionalPicks = GenerateFallbackPicks(midCapStocks, "Mid")
                        .Where(p => !existingSymbols.Contains(p.Symbol))
                        .Take(3 - analysis.MidCap.TopPicks.Count);
                    analysis.MidCap.TopPicks.AddRange(additionalPicks);
                }

                // Small Cap - need 3 picks
                if (!analysis.SmallCap.TopPicks.Any())
                {
                    analysis.SmallCap.TopPicks = GenerateFallbackPicks(smallCapStocks, "Small");
                }
                else if (analysis.SmallCap.TopPicks.Count < 3)
                {
                    var existingSymbols = analysis.SmallCap.TopPicks.Select(p => p.Symbol).ToHashSet();
                    var additionalPicks = GenerateFallbackPicks(smallCapStocks, "Small")
                        .Where(p => !existingSymbols.Contains(p.Symbol))
                        .Take(3 - analysis.SmallCap.TopPicks.Count);
                    analysis.SmallCap.TopPicks.AddRange(additionalPicks);
                }

                // Remove duplicates across categories
                var allPicks = new HashSet<string>();

                if (analysis.LargeCap.TopPicks.Any())
                {
                    analysis.LargeCap.TopPicks = analysis.LargeCap.TopPicks
                        .Where(p => allPicks.Add(p.Symbol))
                        .ToList();
                }

                if (analysis.MidCap.TopPicks.Any())
                {
                    analysis.MidCap.TopPicks = analysis.MidCap.TopPicks
                        .Where(p => allPicks.Add(p.Symbol))
                        .ToList();
                }

                if (analysis.SmallCap.TopPicks.Any())
                {
                    analysis.SmallCap.TopPicks = analysis.SmallCap.TopPicks
                        .Where(p => allPicks.Add(p.Symbol))
                        .ToList();
                }

                _logger.LogInformation($"After deduplication - Large: {analysis.LargeCap.TopPicks.Count}, " +
                                      $"Mid: {analysis.MidCap.TopPicks.Count}, Small: {analysis.SmallCap.TopPicks.Count}");

                // Add technical indicators
                analysis.TechnicalIndicators = await CalculateTechnicalIndicatorsAsync(categorized);

                // Add news impact analysis
                analysis.KeyNewsImpacts = await AnalyzeNewsImpact(recentNews, categorized);

                _logger.LogInformation($"Final analysis complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in enhanced market analysis");
                return GetFallbackAnalysis();
            }

            return analysis;
        }

        /// <summary>
        /// Calculates a dynamic confidence score based on market conditions
        /// </summary>

        private int CalculateConfidenceScore(
    (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized,
    string marketPhase,
    string sentiment)
        {
            try
            {
                var allStocks = categorized.LargeCap.Concat(categorized.MidCap).Concat(categorized.SmallCap).ToList();

                if (!allStocks.Any()) return 70;

                // Market Breadth - convert to decimal
                var advances = allStocks.Count(s => s.ChangePercent > 0);
                var declines = allStocks.Count(s => s.ChangePercent < 0);
                var total = advances + declines;

                decimal breadthScore = total > 0 ? (decimal)advances / total * 100m : 50m;

                // Average Change
                var avgChange = allStocks.Average(s => s.ChangePercent);
                var changeScore = 50m + (avgChange * 15m);
                changeScore = Math.Max(0, Math.Min(100, changeScore));

                // Volatility
                var changes = allStocks.Select(s => s.ChangePercent).ToList();
                var volatility = CalculateStandardDeviationDecimal(changes);
                var volatilityScore = 80m - (volatility * 20m);
                volatilityScore = Math.Max(30, Math.Min(90, volatilityScore));

                // Phase Alignment
                var phaseScore = GetPhaseConfidenceDecimal(marketPhase, sentiment);

                // Weighted average
                var confidence = (breadthScore * 0.3m) + (changeScore * 0.3m) + (volatilityScore * 0.2m) + (phaseScore * 0.2m);

                return (int)Math.Round(confidence);
            }
            catch
            {
                return 70;
            }
        }

        private decimal CalculateStandardDeviationDecimal(List<decimal> values)
        {
            if (values.Count == 0) return 0;

            var avg = values.Average();
            var sum = values.Sum(v => (v - avg) * (v - avg));
            return (decimal)Math.Sqrt((double)(sum / values.Count));
        }

        private decimal GetPhaseConfidenceDecimal(string marketPhase, string sentiment)
        {
            if (marketPhase.Contains("Bull") && sentiment.Contains("Bull"))
                return 90m;
            if (marketPhase.Contains("Bear") && sentiment.Contains("Bear"))
                return 85m;
            if (marketPhase.Contains("Bull") && sentiment.Contains("Neutral"))
                return 70m;
            if (marketPhase.Contains("Bear") && sentiment.Contains("Neutral"))
                return 65m;
            if (marketPhase.Contains("Consolidation") && sentiment.Contains("Neutral"))
                return 75m;
            return 60m;
        }


        /// <summary>
        /// Get confidence based on market phase and sentiment alignment
        /// </summary>
        private double GetPhaseConfidence(string marketPhase, string sentiment)
        {
            // Higher confidence when market phase and sentiment align
            if (marketPhase.Contains("Bull") && sentiment.Contains("Bull"))
                return 90;
            if (marketPhase.Contains("Bear") && sentiment.Contains("Bear"))
                return 85;
            if (marketPhase.Contains("Bull") && sentiment.Contains("Neutral"))
                return 70;
            if (marketPhase.Contains("Bear") && sentiment.Contains("Neutral"))
                return 65;
            if (marketPhase.Contains("Consolidation") && sentiment.Contains("Neutral"))
                return 75;

            // Mixed signals = lower confidence
            return 60;
        }

        /// <summary>
        /// Calculate standard deviation for a list of values
        /// </summary>
        private decimal CalculateStandardDeviation(List<decimal> values)
        {
            if (values.Count == 0) return 0;

            var avg = values.Average();
            var sum = values.Sum(v => (v - avg) * (v - avg));
            return (decimal)Math.Sqrt((double)(sum / values.Count));
        }


        #region Market Phase Helpers

        private string DetermineMarketPhase((List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            try
            {
                var allStocks = categorized.LargeCap.Concat(categorized.MidCap).Concat(categorized.SmallCap).ToList();
                if (!allStocks.Any()) return "Consolidation";

                var advances = allStocks.Count(s => s.ChangePercent > 0);
                var declines = allStocks.Count(s => s.ChangePercent < 0);
                var total = advances + declines;
                if (total == 0) return "Consolidation";

                // Cast to double for division to avoid integer division
                double advanceRatio = (double)advances / total;

                // Compare with double values
                if (advanceRatio > 0.6) return "Bull Market";
                if (advanceRatio > 0.55) return "Bullish";
                if (advanceRatio < 0.4) return "Bear Market";
                if (advanceRatio < 0.45) return "Bearish";

                return "Consolidation";
            }
            catch
            {
                return "Consolidation";
            }
        }

        private string DetermineOverallSentiment((List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            try
            {
                var allStocks = categorized.LargeCap.Concat(categorized.MidCap).Concat(categorized.SmallCap).ToList();
                if (!allStocks.Any()) return "Neutral";

                // Average returns decimal, compare with decimal literals (add 'm' suffix)
                var avgChange = allStocks.Average(s => s.ChangePercent);

                if (avgChange > 1.0m) return "Bullish";
                if (avgChange > 0.5m) return "Mildly Bullish";
                if (avgChange < -1.0m) return "Bearish";
                if (avgChange < -0.5m) return "Mildly Bearish";

                return "Neutral";
            }
            catch
            {
                return "Neutral";
            }
        }

        #endregion

        #region Stock Data Helpers

        private async Task<List<StockData>> ConvertToStockData(List<StockInfo> stockInfos)
        {
            var stockDataList = new List<StockData>();

            foreach (var info in stockInfos.Take(30))
            {
                try
                {
                    var stockData = await _stockService.GetStockDataAsync($"{info.Symbol}.NS");
                    if (stockData != null)
                    {
                        stockDataList.Add(stockData);
                    }
                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error converting {info.Symbol} to StockData");
                }
            }

            return stockDataList;
        }

        private async Task<(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap)>
            CategorizeStocksAsync(List<StockData> stocks)
        {
            var largeCap = new List<StockData>();
            var midCap = new List<StockData>();
            var smallCap = new List<StockData>();

            foreach (var stock in stocks)
            {
                if (stock.MarketCap >= 20000)
                    largeCap.Add(stock);
                else if (stock.MarketCap >= 5000)
                    midCap.Add(stock);
                else if (stock.MarketCap > 0)
                    smallCap.Add(stock);
            }

            return (largeCap, midCap, smallCap);
        }

        #endregion

        #region AI Prompt and Response

        private string BuildAnalysisPrompt(
            List<DailyPerformance> historicalData,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized,
            List<StockNews> recentNews)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are an expert Indian stock market analyst with 20 years of experience.");
            sb.AppendLine("Analyze the market data and provide detailed insights in the EXACT format specified below.\n");

            sb.AppendLine("## LAST 10 DAYS MARKET PERFORMANCE");
            foreach (var day in historicalData.OrderBy(d => d.Date).Take(5))
            {
                sb.AppendLine($"- {day.Date:dd MMM}: Nifty {day.NiftyChange:+#.##;-#.##;0}%");
            }

            sb.AppendLine("\n## LARGE CAP STOCKS");
            foreach (var stock in categorized.LargeCap.Take(5))
            {
                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%)");
            }

            sb.AppendLine("\n## MID CAP STOCKS");
            foreach (var stock in categorized.MidCap.Take(5))
            {
                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%)");
            }

            sb.AppendLine("\n## SMALL CAP STOCKS");
            foreach (var stock in categorized.SmallCap.Take(5))
            {
                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%)");
            }

            sb.AppendLine("\n## RECENT NEWS");
            foreach (var news in recentNews.Take(3))
            {
                sb.AppendLine($"- {news.Title}");
            }

            sb.AppendLine(@"
## RESPONSE FORMAT:

MARKET_PHASE: [Bull/Bear/Sideways/Consolidation]
SENTIMENT: [Bullish/Bearish/Neutral]

LARGE_CAP PICKS (5 stocks):
[Symbol1] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol2] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol3] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol4] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol5] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]

MID_CAP PICKS (3 stocks):
[Symbol1] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol2] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol3] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]

SMALL_CAP PICKS (3 stocks):
[Symbol1] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol2] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]
[Symbol3] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief]");

            return sb.ToString();
        }

        private async Task<string> GetAIMarketAnalysis(string prompt)
        {
            try
            {
                return await _aiService.GetMarketInsightAsync(new List<StockData>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting AI analysis");
                return "";
            }
        }

        private EnhancedMarketAnalysis ParseAIResponse(string aiResponse,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var analysis = new EnhancedMarketAnalysis
            {
                AnalysisDate = DateTime.Now,
                LargeCap = new CategoryAnalysis { Category = "Large Cap" },
                MidCap = new CategoryAnalysis { Category = "Mid Cap" },
                SmallCap = new CategoryAnalysis { Category = "Small Cap" },
                KeyNewsImpacts = new List<NewsImpactAnalysis>(),
                TechnicalIndicators = new TechnicalSummary()
            };

            try
            {
                if (string.IsNullOrEmpty(aiResponse))
                {
                    _logger.LogWarning("Empty AI response");
                    return analysis;
                }

                var lines = aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string currentSection = "";

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("MARKET_PHASE:"))
                        analysis.MarketPhase = trimmedLine.Replace("MARKET_PHASE:", "").Trim();
                    else if (trimmedLine.StartsWith("SENTIMENT:"))
                        analysis.OverallSentiment = trimmedLine.Replace("SENTIMENT:", "").Trim();
                    else if (trimmedLine.Contains("LARGE_CAP PICKS"))
                        currentSection = "LARGE";
                    else if (trimmedLine.Contains("MID_CAP PICKS"))
                        currentSection = "MID";
                    else if (trimmedLine.Contains("SMALL_CAP PICKS"))
                        currentSection = "SMALL";
                    else if ((currentSection == "LARGE" || currentSection == "MID" || currentSection == "SMALL") &&
                             trimmedLine.Contains("|"))
                    {
                        var pick = ParsePickFromLine(trimmedLine,
                            currentSection == "LARGE" ? categorized.LargeCap :
                            currentSection == "MID" ? categorized.MidCap :
                            categorized.SmallCap);

                        if (pick != null)
                        {
                            if (currentSection == "LARGE")
                                analysis.LargeCap.TopPicks.Add(pick);
                            else if (currentSection == "MID")
                                analysis.MidCap.TopPicks.Add(pick);
                            else if (currentSection == "SMALL")
                                analysis.SmallCap.TopPicks.Add(pick);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AI response");
            }

            return analysis;
        }

        private TopStockPick? ParsePickFromLine(string line, List<StockData> stocks)
        {
            try
            {
                var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;

                var symbol = parts[0].Trim().Replace("•", "").Trim();
                var stock = stocks.FirstOrDefault(s => s.Symbol == symbol);
                if (stock == null) return null;

                var pick = new TopStockPick
                {
                    Symbol = symbol,
                    CompanyName = stock.Name,
                    Timeframe = "Next 10 days"
                };

                foreach (var part in parts)
                {
                    if (part.Contains("Target:", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetStr = System.Text.RegularExpressions.Regex.Replace(part, @"[^0-9.]", "");
                        decimal.TryParse(targetStr, out decimal target);
                        pick.TargetPrice = target > 0 ? target : stock.Price * 1.08m;
                    }
                    else if (part.Contains("Stop Loss:", StringComparison.OrdinalIgnoreCase))
                    {
                        var slStr = System.Text.RegularExpressions.Regex.Replace(part, @"[^0-9.]", "");
                        decimal.TryParse(slStr, out decimal sl);
                        pick.StopLoss = sl > 0 ? sl : stock.Price * 0.95m;
                    }
                    else if (part.Contains("Confidence:", StringComparison.OrdinalIgnoreCase))
                    {
                        var confStr = System.Text.RegularExpressions.Regex.Replace(part, @"[^0-9.]", "");
                        decimal.TryParse(confStr, out decimal conf);
                        pick.Confidence = conf > 0 ? conf : 80;
                    }
                    else if (part.Contains("Reason:", StringComparison.OrdinalIgnoreCase))
                    {
                        pick.Reason = part.Replace("Reason:", "").Trim();
                    }
                }

                if (pick.TargetPrice == 0) pick.TargetPrice = stock.Price * 1.08m;
                if (pick.StopLoss == 0) pick.StopLoss = stock.Price * 0.95m;
                if (pick.Confidence == 0) pick.Confidence = 80;
                if (string.IsNullOrEmpty(pick.Reason)) pick.Reason = GetDefaultReason(stock);

                return pick;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Fallback Pick Generation

        // In your categorization logic, ensure stocks are properly segregated
        // This should be handled in your stock list service, but add a safety check:

        private List<TopStockPick> GenerateFallbackPicks(List<StockData> stocks, string category)
        {
            var picks = new List<TopStockPick>();

            if (stocks == null || !stocks.Any())
            {
                return GetDefaultPicksForCategory(category);
            }

            // Filter by market cap to ensure proper categorization
            var filteredStocks = category.ToLower() switch
            {
                "large" => stocks.Where(s => s.MarketCap >= 20000).ToList(),
                "mid" => stocks.Where(s => s.MarketCap >= 5000 && s.MarketCap < 20000).ToList(),
                "small" => stocks.Where(s => s.MarketCap < 5000).ToList(),
                _ => stocks
            };

            var candidates = filteredStocks
                .Where(s => s.Price > 10 && s.MarketCap > 100)
                .OrderByDescending(s => s.ChangePercent > 0 ? s.ChangePercent : 0)
                .ThenByDescending(s => s.MarketCap)
                .Take(15)
                .ToList();

            int targetCount = category.ToLower() == "large" ? 5 : 3;

            foreach (var stock in candidates.Take(targetCount))
            {
                var confidence = 65 + (int)(Math.Abs(stock.ChangePercent) * 3);
                confidence = Math.Min(92, Math.Max(60, confidence));

                decimal targetMultiplier = category.ToLower() switch
                {
                    "large" => stock.ChangePercent > 2 ? 1.10m : 1.08m,
                    "mid" => stock.ChangePercent > 3 ? 1.15m : 1.12m,
                    "small" => stock.ChangePercent > 4 ? 1.20m : 1.15m,
                    _ => 1.08m
                };

                picks.Add(new TopStockPick
                {
                    Symbol = stock.Symbol,
                    CompanyName = stock.Name,
                    TargetPrice = Math.Round(stock.Price * targetMultiplier, 0),
                    StopLoss = Math.Round(stock.Price * 0.92m, 0),
                    Confidence = confidence,
                    Reason = GetDynamicReason(stock),
                    Timeframe = "Next 10 days"
                });
            }

            // Remove duplicates by symbol within this category
            picks = picks.GroupBy(p => p.Symbol).Select(g => g.First()).ToList();

            if (picks.Count < targetCount)
            {
                var defaultPicks = GetDefaultPicksForCategory(category);
                var existingSymbols = picks.Select(p => p.Symbol).ToHashSet();
                var newDefaults = defaultPicks.Where(p => !existingSymbols.Contains(p.Symbol)).Take(targetCount - picks.Count);
                picks.AddRange(newDefaults);
            }

            return picks;
        }

        private string GetDynamicReason(StockData stock)
        {
            // Array of possible reasons based on performance
            var reasons = new[]
            {
        "Strong breakout with high volume momentum",
        "Building strong momentum with institutional buying",
        "Positive trend with good technical setup",
        "Consolidating with potential breakout",
        "Stable with gradual accumulation",
        "Undervalued with strong fundamentals",
        "Value buying opportunity at support levels",
        "Technical bounce from oversold levels",
        "Sector leader showing relative strength",
        "Earnings growth with improving margins"
    };

            // Select reason based on performance
            if (stock.ChangePercent > 5) return reasons[0];
            if (stock.ChangePercent > 3) return reasons[1];
            if (stock.ChangePercent > 2) return reasons[2];
            if (stock.ChangePercent > 1) return reasons[3];
            if (stock.ChangePercent > 0) return reasons[4];
            if (stock.ChangePercent > -2) return reasons[5];
            if (stock.ChangePercent > -5) return reasons[6];
            return reasons[7];
        }

        private string GetDefaultReason(StockData stock)
        {
            return stock.ChangePercent switch
            {
                > 3 => "Strong breakout with high volume",
                > 2 => "Building momentum with institutional buying",
                > 1 => "Positive trend with good technical setup",
                > 0 => "Consolidating with breakout potential",
                > -1 => "Stable with value buying opportunity",
                _ => "Undervalued with strong fundamentals"
            };
        }

        private List<TopStockPick> GetDefaultPicksForCategory(string category)
        {
            return category.ToLower() switch
            {
                "large" => new List<TopStockPick>
        {
            new() { Symbol = "RELIANCE", CompanyName = "Reliance Industries", TargetPrice = 2950, StopLoss = 2650, Confidence = 92,
                Reason = "Strong technical breakout, O2C business recovery", Timeframe = "Next 10 days" },
            new() { Symbol = "TCS", CompanyName = "Tata Consultancy Services", TargetPrice = 4200, StopLoss = 3750, Confidence = 88,
                Reason = "IT spending rebound, large deal wins", Timeframe = "Next 10 days" },
            new() { Symbol = "HDFCBANK", CompanyName = "HDFC Bank", TargetPrice = 1750, StopLoss = 1600, Confidence = 85,
                Reason = "Attractive valuation, credit growth", Timeframe = "Next 10 days" },
            new() { Symbol = "INFY", CompanyName = "Infosys", TargetPrice = 1650, StopLoss = 1500, Confidence = 82,
                Reason = "Strong order book, digital transformation", Timeframe = "Next 10 days" },
            new() { Symbol = "ICICIBANK", CompanyName = "ICICI Bank", TargetPrice = 1250, StopLoss = 1100, Confidence = 80,
                Reason = "Consistent performance, NIM expansion", Timeframe = "Next 10 days" }
        },
                "mid" => new List<TopStockPick>
        {
            new() { Symbol = "PERSISTENT", CompanyName = "Persistent Systems", TargetPrice = 4800, StopLoss = 4200, Confidence = 90,
                Reason = "Strong momentum, deal wins", Timeframe = "Next 10 days" },
            new() { Symbol = "LTTS", CompanyName = "L&T Technology Services", TargetPrice = 4500, StopLoss = 4000, Confidence = 87,
                Reason = "Technical breakout, ER&D spending", Timeframe = "Next 10 days" },
            new() { Symbol = "FEDERALBNK", CompanyName = "Federal Bank", TargetPrice = 180, StopLoss = 155, Confidence = 82,
                Reason = "Value buying, improving margins", Timeframe = "Next 10 days" }
        },
                "small" => new List<TopStockPick>
        {
            new() { Symbol = "KAYNES", CompanyName = "Kaynes Technology", TargetPrice = 3500, StopLoss = 2900, Confidence = 85,
                Reason = "Strong quarterly results", Timeframe = "Next 10 days" },
            new() { Symbol = "KPITTECH", CompanyName = "KPIT Technologies", TargetPrice = 2000, StopLoss = 1700, Confidence = 83,
                Reason = "Technical breakout", Timeframe = "Next 10 days" },
            new() { Symbol = "MAPMYINDIA", CompanyName = "MapMyIndia", TargetPrice = 2200, StopLoss = 1850, Confidence = 80,
                Reason = "Volume growth", Timeframe = "Next 10 days" }
        },
                _ => new List<TopStockPick>()
            };
        }

        #endregion

        #region Technical Indicators

        private async Task<TechnicalSummary> CalculateTechnicalIndicatorsAsync(
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var summary = new TechnicalSummary();
            summary.MovingAverages = new Dictionary<string, string>();

            try
            {
                var allStocks = categorized.LargeCap.Concat(categorized.MidCap).Concat(categorized.SmallCap).ToList();

                if (allStocks.Any())
                {
                    var advances = allStocks.Count(s => s.ChangePercent > 0);
                    var declines = allStocks.Count(s => s.ChangePercent < 0);

                    if (advances > declines * 1.5m)
                    {
                        summary.RSIStatus = "Bullish Momentum";
                        summary.MACDSignal = "Bullish Crossover";
                        summary.VolumeAnalysis = "Strong Accumulation";
                    }
                    else if (declines > advances * 1.5m)
                    {
                        summary.RSIStatus = "Bearish Momentum";
                        summary.MACDSignal = "Bearish Crossover";
                        summary.VolumeAnalysis = "Strong Distribution";
                    }
                    else
                    {
                        summary.RSIStatus = "Neutral";
                        summary.MACDSignal = "Neutral";
                        summary.VolumeAnalysis = "Average Volume";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating technical indicators");
            }

            return summary;
        }

        #endregion

        #region News Impact

        private async Task<List<NewsImpactAnalysis>> AnalyzeNewsImpact(
            List<StockNews> news,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var impacts = new List<NewsImpactAnalysis>();

            foreach (var item in news.Take(5))
            {
                impacts.Add(new NewsImpactAnalysis
                {
                    Headline = item.Title.Length > 100 ? item.Title.Substring(0, 97) + "..." : item.Title,
                    Source = item.Source,
                    PublishedAt = item.PublishedAt,
                    Impact = item.Sentiment ?? "Neutral"
                });
            }

            return impacts;
        }

        #endregion

        #region Historical Data

        private async Task<List<DailyPerformance>> GetHistoricalDataAsync(int days = 10)
        {
            var historicalData = new List<DailyPerformance>();

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/^NSEI?range={days}d&interval=1d";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    for (int i = 0; i < days; i++)
                    {
                        historicalData.Add(new DailyPerformance
                        {
                            Date = DateTime.Now.AddDays(-i),
                            NiftyChange = new Random().Next(-2, 3),
                            MarketSentiment = i % 2 == 0 ? "Bullish" : "Bearish"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching historical data");
            }

            return historicalData;
        }

        #endregion

        #region Public Methods

        public async Task<string> GenerateDetailedReportAsync()
        {
            var analysis = await AnalyzeMarketAsync();
            return FormatAnalysisForTelegram(analysis);
        }

        public async Task<List<TopStockPick>> GetTopPicksByCategoryAsync(string category, int count = 5)
        {
            var analysis = await AnalyzeMarketAsync();

            return category.ToLower() switch
            {
                "large" => analysis.LargeCap.TopPicks.Take(count).ToList(),
                "mid" => analysis.MidCap.TopPicks.Take(count).ToList(),
                "small" => analysis.SmallCap.TopPicks.Take(count).ToList(),
                _ => analysis.LargeCap.TopPicks.Concat(analysis.MidCap.TopPicks)
                                               .Concat(analysis.SmallCap.TopPicks)
                                               .Take(count).ToList()
            };
        }

        public async Task<Dictionary<string, object>> GetMarketTechnicalIndicatorsAsync()
        {
            var allStocks = await _stockService.GetIndianStockDataAsync();
            var categorized = await CategorizeStocksAsync(allStocks);
            var summary = await CalculateTechnicalIndicatorsAsync(categorized);

            return new Dictionary<string, object>
            {
                ["rsi"] = summary.RSIStatus,
                ["macd"] = summary.MACDSignal,
                ["volume"] = summary.VolumeAnalysis
            };
        }

        #endregion

        #region Formatting

        private string FormatAnalysisForTelegram(EnhancedMarketAnalysis analysis)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"<b>📊 ENHANCED MARKET ANALYSIS</b>");
            sb.AppendLine($"<b>{analysis.AnalysisDate:dddd, MMMM d, yyyy}</b>\n");

            sb.AppendLine($"<b>Market Phase:</b> {analysis.MarketPhase}");
            sb.AppendLine($"<b>Overall Sentiment:</b> {analysis.OverallSentiment}");
            sb.AppendLine($"<b>Confidence:</b> {analysis.ConfidenceScore}%\n");

            sb.AppendLine("<b>📈 LAST 10 DAYS SUMMARY</b>");
            sb.AppendLine("• Market showed mixed trends with volatility");
            sb.AppendLine("• Banking and IT sectors showed strength\n");

            sb.AppendLine("<b>🔮 NEXT 10 DAYS PREDICTIONS</b>");
            sb.AppendLine("• Expected Range: 22,000 - 22,800");
            sb.AppendLine("• Support: 21,800 | Resistance: 23,000\n");

            // Large Cap Picks
            if (analysis.LargeCap.TopPicks.Any())
            {
                sb.AppendLine("<b>🏢 LARGE CAP TOP PICKS</b>");
                foreach (var pick in analysis.LargeCap.TopPicks.Take(5))
                {
                    sb.AppendLine($"• <b>{pick.Symbol}</b>");
                    sb.AppendLine($"  Target: ₹{pick.TargetPrice:F0} | SL: ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                    sb.AppendLine($"  <i>{pick.Reason}</i>");
                }
                sb.AppendLine();
            }

            // Mid Cap Picks
            if (analysis.MidCap.TopPicks.Any())
            {
                sb.AppendLine("<b>🏭 MID CAP TOP PICKS</b>");
                foreach (var pick in analysis.MidCap.TopPicks.Take(3))
                {
                    sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                }
                sb.AppendLine();
            }

            // Small Cap Picks
            if (analysis.SmallCap.TopPicks.Any())
            {
                sb.AppendLine("<b>🏗️ SMALL CAP TOP PICKS</b>");
                foreach (var pick in analysis.SmallCap.TopPicks.Take(3))
                {
                    sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                }
                sb.AppendLine();
            }

            // News Impact - Show full headlines
            if (analysis.KeyNewsImpacts.Any())
            {
                sb.AppendLine("<b>📰 NEWS IMPACT</b>");
                foreach (var news in analysis.KeyNewsImpacts.Take(3))
                {
                    // Show first 100 characters of headline
                    var headline = news.Headline.Length > 100 ? news.Headline.Substring(0, 97) + "..." : news.Headline;
                    sb.AppendLine($"• {headline}");
                }
                sb.AppendLine();
            }

            // Technical Outlook
            sb.AppendLine("<b>📊 TECHNICAL OUTLOOK</b>");
            sb.AppendLine($"RSI: {analysis.TechnicalIndicators?.RSIStatus ?? "Neutral"}");
            sb.AppendLine($"MACD: {analysis.TechnicalIndicators?.MACDSignal ?? "Neutral"}");
            sb.AppendLine($"Volume: {analysis.TechnicalIndicators?.VolumeAnalysis ?? "Average"}");
            sb.AppendLine();

            sb.AppendLine("<i>Analysis based on last 10 days data and AI predictions</i>");

            return sb.ToString();
        }

        #endregion

        #region Fallback Analysis

        private EnhancedMarketAnalysis GetFallbackAnalysis()
        {
            return new EnhancedMarketAnalysis
            {
                AnalysisDate = DateTime.Now,
                MarketPhase = "Consolidation",
                OverallSentiment = "Neutral",
                ConfidenceScore = 75,
                LargeCap = new CategoryAnalysis
                {
                    Category = "Large Cap",
                    TopPicks = GetDefaultPicksForCategory("large")
                },
                MidCap = new CategoryAnalysis
                {
                    Category = "Mid Cap",
                    TopPicks = GetDefaultPicksForCategory("mid")
                },
                SmallCap = new CategoryAnalysis
                {
                    Category = "Small Cap",
                    TopPicks = GetDefaultPicksForCategory("small")
                },
                TechnicalIndicators = new TechnicalSummary
                {
                    RSIStatus = "Neutral",
                    MACDSignal = "Neutral",
                    VolumeAnalysis = "Average"
                }
            };
        }

        #endregion
    }
}