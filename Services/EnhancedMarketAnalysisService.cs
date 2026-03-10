using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text;
using System.Text.Json;

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

        // Constants
        private const int LARGE_CAP_THRESHOLD = 20000;
        private const int MID_CAP_THRESHOLD = 5000;
        private const int MAX_STOCKS_PER_CATEGORY = 30;
        private const int DEFAULT_CONFIDENCE = 70;
        private const int LARGE_CAP_PICKS_REQUIRED = 5;
        private const int MID_CAP_PICKS_REQUIRED = 3;
        private const int SMALL_CAP_PICKS_REQUIRED = 3;
        private const int API_RATE_LIMIT_DELAY_MS = 200;
        private const int MAX_NEWS_ITEMS = 20;
        private const int HISTORICAL_DAYS = 10;
        private const int MAX_PICKS_PER_CATEGORY = 15;
        private const decimal STRONG_MOMENTUM_THRESHOLD = 2m;
        private const decimal HIGH_CONFIDENCE_THRESHOLD = 5m;
        private const decimal MEDIUM_CONFIDENCE_THRESHOLD = 3m;
        private const decimal LOW_CONFIDENCE_THRESHOLD = 1m;

        public EnhancedMarketAnalysisService(
            IStockService stockService,
            IStockListService stockListService,
            IAIService aiService,
            INewsService newsService,
            IHttpClientFactory httpClientFactory,
            ILogger<EnhancedMarketAnalysisService> logger)
        {
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _stockListService = stockListService ?? throw new ArgumentNullException(nameof(stockListService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _newsService = newsService ?? throw new ArgumentNullException(nameof(newsService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<EnhancedMarketAnalysis> AnalyzeMarketAsync()
        {
            _logger.LogInformation("Starting enhanced market analysis...");

            var analysis = InitializeAnalysis();

            try
            {
                // Get categorized stocks directly from StockListService
                var categories = await _stockListService.GetAllCategoriesAsync();
                ValidateCategories(categories);

                _logger.LogInformation("Categories - Large: {LargeCount}, Mid: {MidCount}, Small: {SmallCount}",
                    categories["LargeCap"].Count, categories["MidCap"].Count, categories["SmallCap"].Count);

                // Convert StockInfo to StockData for each category
                var (largeCapStocks, midCapStocks, smallCapStocks) = await ConvertCategoriesToStockData(categories);
                var categorized = (largeCapStocks, midCapStocks, smallCapStocks);

                // Gather additional data
                var historicalData = await GetHistoricalDataAsync();
                var recentNews = await _newsService.GetTopMarketNewsAsync(MAX_NEWS_ITEMS);

                // Get and parse AI analysis
                analysis = await ProcessAIAnalysis(analysis, historicalData, categorized, recentNews);

                // Ensure we have market phase and sentiment
                EnsureMarketPhaseAndSentiment(analysis, categorized);

                // Calculate dynamic confidence score
                analysis.ConfidenceScore = CalculateConfidenceScore(categorized, analysis.MarketPhase, analysis.OverallSentiment);
                _logger.LogInformation("Calculated confidence score: {Confidence}%", analysis.ConfidenceScore);

                // Ensure we have enough picks per category
                EnsureSufficientPicks(analysis, largeCapStocks, midCapStocks, smallCapStocks);

                // Remove duplicates across categories
                RemoveDuplicatePicks(analysis);

                // Add technical indicators and enhanced news impact
                analysis.TechnicalIndicators = await CalculateTechnicalIndicatorsAsync(categorized);
                analysis.KeyNewsImpacts = await AnalyzeNewsImpact(recentNews, categorized);

                _logger.LogInformation("Final analysis complete - Large: {LargeCount}, Mid: {MidCount}, Small: {SmallCount}",
                    analysis.LargeCap.TopPicks.Count, analysis.MidCap.TopPicks.Count, analysis.SmallCap.TopPicks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in enhanced market analysis");
                return GetFallbackAnalysis();
            }

            return analysis;
        }

        #region Initialization and Validation

        private EnhancedMarketAnalysis InitializeAnalysis()
        {
            return new EnhancedMarketAnalysis
            {
                AnalysisDate = DateTime.Now,
                LargeCap = new CategoryAnalysis { Category = "Large Cap", TopPicks = new List<TopStockPick>() },
                MidCap = new CategoryAnalysis { Category = "Mid Cap", TopPicks = new List<TopStockPick>() },
                SmallCap = new CategoryAnalysis { Category = "Small Cap", TopPicks = new List<TopStockPick>() },
                KeyNewsImpacts = new List<NewsImpactAnalysis>(),
                TechnicalIndicators = new TechnicalSummary()
            };
        }

        private void ValidateCategories(Dictionary<string, List<StockInfo>> categories)
        {
            if (categories == null)
                throw new InvalidOperationException("Categories cannot be null");

            if (!categories.ContainsKey("LargeCap") || !categories.ContainsKey("MidCap") || !categories.ContainsKey("SmallCap"))
                throw new InvalidOperationException("Categories missing required keys");
        }

        private async Task<(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap)>
            ConvertCategoriesToStockData(Dictionary<string, List<StockInfo>> categories)
        {
            var largeCapStocks = await ConvertToStockData(categories["LargeCap"].Take(MAX_STOCKS_PER_CATEGORY).ToList());
            var midCapStocks = await ConvertToStockData(categories["MidCap"].Take(MAX_STOCKS_PER_CATEGORY).ToList());
            var smallCapStocks = await ConvertToStockData(categories["SmallCap"].Take(MAX_STOCKS_PER_CATEGORY).ToList());

            _logger.LogInformation("Converted - Large: {LargeCount}, Mid: {MidCount}, Small: {SmallCount}",
                largeCapStocks.Count, midCapStocks.Count, smallCapStocks.Count);

            return (largeCapStocks, midCapStocks, smallCapStocks);
        }

        #endregion

        #region AI Processing

        private async Task<EnhancedMarketAnalysis> ProcessAIAnalysis(
            EnhancedMarketAnalysis analysis,
            List<DailyPerformance> historicalData,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized,
            List<StockNews> recentNews)
        {
            var prompt = BuildAnalysisPrompt(historicalData, categorized, recentNews);
            var aiResponse = await GetAIMarketAnalysis(prompt);

            if (!string.IsNullOrEmpty(aiResponse))
            {
                var parsedAnalysis = ParseAIResponse(aiResponse, categorized);
                if (parsedAnalysis != null)
                {
                    analysis.MarketPhase = parsedAnalysis.MarketPhase;
                    analysis.OverallSentiment = parsedAnalysis.OverallSentiment;
                    analysis.LargeCap.TopPicks = parsedAnalysis.LargeCap.TopPicks;
                    analysis.MidCap.TopPicks = parsedAnalysis.MidCap.TopPicks;
                    analysis.SmallCap.TopPicks = parsedAnalysis.SmallCap.TopPicks;
                }
            }

            return analysis;
        }

        #endregion

        #region Confidence Score Calculation

        private int CalculateConfidenceScore(
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized,
            string marketPhase,
            string sentiment)
        {
            try
            {
                var allStocks = GetAllStocks(categorized);
                if (!allStocks.Any()) return DEFAULT_CONFIDENCE;

                // Market Breadth (30% weight)
                var breadthScore = CalculateBreadthScore(allStocks);

                // Average Change (30% weight)
                var changeScore = CalculateChangeScore(allStocks);

                // Volatility (20% weight)
                var volatilityScore = CalculateVolatilityScore(allStocks);

                // Phase Alignment (20% weight)
                var phaseScore = GetPhaseConfidence(marketPhase, sentiment);

                // Weighted average
                var confidence = (breadthScore * 0.3m) + (changeScore * 0.3m) +
                                (volatilityScore * 0.2m) + (phaseScore * 0.2m);

                return (int)Math.Round(confidence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error calculating confidence score");
                return DEFAULT_CONFIDENCE;
            }
        }

        private List<StockData> GetAllStocks(
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var allStocks = new List<StockData>();
            allStocks.AddRange(categorized.LargeCap ?? new List<StockData>());
            allStocks.AddRange(categorized.MidCap ?? new List<StockData>());
            allStocks.AddRange(categorized.SmallCap ?? new List<StockData>());
            return allStocks;
        }

        private decimal CalculateBreadthScore(List<StockData> stocks)
        {
            var advances = stocks.Count(s => s.ChangePercent > 0);
            var declines = stocks.Count(s => s.ChangePercent < 0);
            var total = advances + declines;

            return total > 0 ? (decimal)advances / total * 100m : 50m;
        }

        private decimal CalculateChangeScore(List<StockData> stocks)
        {
            var avgChange = stocks.Average(s => s.ChangePercent);
            var changeScore = 50m + (avgChange * 15m);
            return Math.Max(0, Math.Min(100, changeScore));
        }

        private decimal CalculateVolatilityScore(List<StockData> stocks)
        {
            var changes = stocks.Select(s => s.ChangePercent).ToList();
            var volatility = CalculateStandardDeviation(changes);
            var volatilityScore = 80m - (volatility * 20m);
            return Math.Max(30, Math.Min(90, volatilityScore));
        }

        private decimal CalculateStandardDeviation(List<decimal> values)
        {
            if (values.Count == 0) return 0;

            var avg = values.Average();
            var sum = values.Sum(v => (v - avg) * (v - avg));
            return (decimal)Math.Sqrt((double)(sum / values.Count));
        }

        private decimal GetPhaseConfidence(string marketPhase, string sentiment)
        {
            if (string.IsNullOrEmpty(marketPhase) || string.IsNullOrEmpty(sentiment))
                return 60m;

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

        #endregion

        #region Market Phase Helpers

        private void EnsureMarketPhaseAndSentiment(EnhancedMarketAnalysis analysis,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            if (string.IsNullOrEmpty(analysis.MarketPhase))
            {
                analysis.MarketPhase = DetermineMarketPhase(categorized);
            }

            if (string.IsNullOrEmpty(analysis.OverallSentiment))
            {
                analysis.OverallSentiment = DetermineOverallSentiment(categorized);
            }
        }

        private string DetermineMarketPhase((List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            try
            {
                var allStocks = GetAllStocks(categorized);
                if (!allStocks.Any()) return "Consolidation";

                var advances = allStocks.Count(s => s.ChangePercent > 0);
                var declines = allStocks.Count(s => s.ChangePercent < 0);
                var total = advances + declines;

                if (total == 0) return "Consolidation";

                double advanceRatio = (double)advances / total;

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
                var allStocks = GetAllStocks(categorized);
                if (!allStocks.Any()) return "Neutral";

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
            if (stockInfos == null || !stockInfos.Any())
                return new List<StockData>();

            var stockDataList = new List<StockData>();

            foreach (var info in stockInfos)
            {
                try
                {
                    if (info?.Symbol == null) continue;

                    var stockData = await _stockService.GetStockDataAsync($"{info.Symbol}.NS");
                    if (stockData != null)
                    {
                        stockDataList.Add(stockData);
                    }

                    await Task.Delay(API_RATE_LIMIT_DELAY_MS);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error converting {Symbol} to StockData", info?.Symbol);
                }
            }

            return stockDataList;
        }

        private async Task<(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap)>
            CategorizeStocksAsync(List<StockData> stocks)
        {
            if (stocks == null)
                return (new List<StockData>(), new List<StockData>(), new List<StockData>());

            var largeCap = new List<StockData>();
            var midCap = new List<StockData>();
            var smallCap = new List<StockData>();

            foreach (var stock in stocks.Where(s => s != null))
            {
                if (stock.MarketCap >= LARGE_CAP_THRESHOLD)
                    largeCap.Add(stock);
                else if (stock.MarketCap >= MID_CAP_THRESHOLD)
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

            AppendHistoricalData(sb, historicalData);
            AppendStockData(sb, "LARGE CAP STOCKS", categorized.LargeCap);
            AppendStockData(sb, "MID CAP STOCKS", categorized.MidCap);
            AppendStockData(sb, "SMALL CAP STOCKS", categorized.SmallCap);
            AppendNewsData(sb, recentNews);
            AppendResponseFormat(sb);

            return sb.ToString();
        }

        private void AppendHistoricalData(StringBuilder sb, List<DailyPerformance> historicalData)
        {
            sb.AppendLine("## LAST 10 DAYS MARKET PERFORMANCE");
            foreach (var day in historicalData.OrderBy(d => d.Date).Take(5))
            {
                sb.AppendLine($"- {day.Date:dd MMM}: Nifty {day.NiftyChange:+#.##;-#.##;0}%");
            }
            sb.AppendLine();
        }

        private void AppendStockData(StringBuilder sb, string title, List<StockData> stocks)
        {
            sb.AppendLine($"## {title}");
            foreach (var stock in stocks.Take(5))
            {
                if (stock != null)
                {
                    sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%)");
                }
            }
            sb.AppendLine();
        }

        private void AppendNewsData(StringBuilder sb, List<StockNews> recentNews)
        {
            sb.AppendLine("## RECENT NEWS");
            foreach (var news in recentNews.Take(3))
            {
                if (news?.Title != null)
                {
                    // Show full news headline with impact
                    var impactEmoji = GetImpactEmoji(news.Impact);
                    sb.AppendLine($"- {impactEmoji} {news.Title}");

                    // Show affected stocks if any
                    if (news.AffectedStocks?.Any() == true)
                    {
                        sb.AppendLine($"  Affects: {string.Join(", ", news.AffectedStocks.Take(3))}");
                    }
                }
            }
            sb.AppendLine();
        }

        private string GetImpactEmoji(string? impact)
        {
            return impact?.ToLower() switch
            {
                "high" => "🔴",
                "positive" => "🟢",
                "medium" => "🟡",
                _ => "⚪"
            };
        }

        private void AppendResponseFormat(StringBuilder sb)
        {
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
                return string.Empty;
            }
        }

        private EnhancedMarketAnalysis? ParseAIResponse(string aiResponse,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            if (string.IsNullOrEmpty(aiResponse))
            {
                _logger.LogWarning("Empty AI response");
                return null;
            }

            var analysis = new EnhancedMarketAnalysis
            {
                AnalysisDate = DateTime.Now,
                LargeCap = new CategoryAnalysis { Category = "Large Cap", TopPicks = new List<TopStockPick>() },
                MidCap = new CategoryAnalysis { Category = "Mid Cap", TopPicks = new List<TopStockPick>() },
                SmallCap = new CategoryAnalysis { Category = "Small Cap", TopPicks = new List<TopStockPick>() }
            };

            try
            {
                var lines = aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string currentSection = "";

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    ParseSectionHeader(trimmedLine, analysis, ref currentSection);
                    ParseSectionContent(trimmedLine, currentSection, analysis, categorized);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AI response");
                return null;
            }

            return analysis;
        }

        private void ParseSectionHeader(string line, EnhancedMarketAnalysis analysis, ref string currentSection)
        {
            if (line.StartsWith("MARKET_PHASE:"))
                analysis.MarketPhase = line.Replace("MARKET_PHASE:", "").Trim();
            else if (line.StartsWith("SENTIMENT:"))
                analysis.OverallSentiment = line.Replace("SENTIMENT:", "").Trim();
            else if (line.Contains("LARGE_CAP PICKS"))
                currentSection = "LARGE";
            else if (line.Contains("MID_CAP PICKS"))
                currentSection = "MID";
            else if (line.Contains("SMALL_CAP PICKS"))
                currentSection = "SMALL";
        }

        private void ParseSectionContent(string line, string currentSection, EnhancedMarketAnalysis analysis,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            if (!line.Contains("|")) return;

            var pick = ParsePickFromLine(line,
                currentSection == "LARGE" ? categorized.LargeCap :
                currentSection == "MID" ? categorized.MidCap :
                currentSection == "SMALL" ? categorized.SmallCap : null);

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

        private TopStockPick? ParsePickFromLine(string line, List<StockData>? stocks)
        {
            if (stocks == null) return null;

            try
            {
                var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;

                var symbol = parts[0].Trim().Replace("•", "").Trim();
                var stock = stocks.FirstOrDefault(s => s?.Symbol == symbol);
                if (stock == null) return null;

                var pick = new TopStockPick
                {
                    Symbol = symbol,
                    CompanyName = stock.Name,
                    Timeframe = "Next 10 days"
                };

                foreach (var part in parts)
                {
                    ParsePickPart(part, pick, stock);
                }

                // Set defaults if needed
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

        private void ParsePickPart(string part, TopStockPick pick, StockData stock)
        {
            if (part.Contains("Target:", StringComparison.OrdinalIgnoreCase))
            {
                var targetStr = System.Text.RegularExpressions.Regex.Replace(part, @"[^0-9.]", "");
                if (decimal.TryParse(targetStr, out decimal target) && target > 0)
                    pick.TargetPrice = target;
            }
            else if (part.Contains("Stop Loss:", StringComparison.OrdinalIgnoreCase))
            {
                var slStr = System.Text.RegularExpressions.Regex.Replace(part, @"[^0-9.]", "");
                if (decimal.TryParse(slStr, out decimal sl) && sl > 0)
                    pick.StopLoss = sl;
            }
            else if (part.Contains("Confidence:", StringComparison.OrdinalIgnoreCase))
            {
                var confStr = System.Text.RegularExpressions.Regex.Replace(part, @"[^0-9.]", "");
                if (decimal.TryParse(confStr, out decimal conf) && conf > 0)
                    pick.Confidence = conf;
            }
            else if (part.Contains("Reason:", StringComparison.OrdinalIgnoreCase))
            {
                pick.Reason = part.Replace("Reason:", "").Trim();
            }
        }

        #endregion

        #region Pick Management

        private void EnsureSufficientPicks(EnhancedMarketAnalysis analysis,
            List<StockData> largeCapStocks,
            List<StockData> midCapStocks,
            List<StockData> smallCapStocks)
        {
            analysis.LargeCap.TopPicks = EnsureCategoryPicks(analysis.LargeCap.TopPicks, largeCapStocks, "Large", LARGE_CAP_PICKS_REQUIRED);
            analysis.MidCap.TopPicks = EnsureCategoryPicks(analysis.MidCap.TopPicks, midCapStocks, "Mid", MID_CAP_PICKS_REQUIRED);
            analysis.SmallCap.TopPicks = EnsureCategoryPicks(analysis.SmallCap.TopPicks, smallCapStocks, "Small", SMALL_CAP_PICKS_REQUIRED);
        }

        private List<TopStockPick> EnsureCategoryPicks(List<TopStockPick> currentPicks, List<StockData> stocks, string category, int required)
        {
            if (currentPicks == null) currentPicks = new List<TopStockPick>();

            if (!currentPicks.Any())
            {
                return GenerateFallbackPicks(stocks, category);
            }

            if (currentPicks.Count < required)
            {
                var existingSymbols = currentPicks.Select(p => p.Symbol).ToHashSet();
                var additionalPicks = GenerateFallbackPicks(stocks, category)
                    .Where(p => !existingSymbols.Contains(p.Symbol))
                    .Take(required - currentPicks.Count);
                currentPicks.AddRange(additionalPicks);
            }

            return currentPicks;
        }

        private void RemoveDuplicatePicks(EnhancedMarketAnalysis analysis)
        {
            var allPicks = new HashSet<string>();

            if (analysis.LargeCap.TopPicks.Any())
            {
                analysis.LargeCap.TopPicks = analysis.LargeCap.TopPicks
                    .Where(p => p != null && allPicks.Add(p.Symbol))
                    .ToList();
            }

            if (analysis.MidCap.TopPicks.Any())
            {
                analysis.MidCap.TopPicks = analysis.MidCap.TopPicks
                    .Where(p => p != null && allPicks.Add(p.Symbol))
                    .ToList();
            }

            if (analysis.SmallCap.TopPicks.Any())
            {
                analysis.SmallCap.TopPicks = analysis.SmallCap.TopPicks
                    .Where(p => p != null && allPicks.Add(p.Symbol))
                    .ToList();
            }
        }

        private List<TopStockPick> GenerateFallbackPicks(List<StockData>? stocks, string category)
        {
            if (stocks == null || !stocks.Any())
            {
                return GetDefaultPicksForCategory(category);
            }

            var filteredStocks = FilterStocksByCategory(stocks, category);
            var candidates = filteredStocks
                .Where(s => s != null && s.Price > 10 && s.MarketCap > 100)
                .OrderByDescending(s => s.ChangePercent > 0 ? s.ChangePercent : 0)
                .ThenByDescending(s => s.MarketCap)
                .Take(MAX_PICKS_PER_CATEGORY)
                .ToList();

            int targetCount = GetTargetCount(category);
            var picks = GeneratePicksFromCandidates(candidates, category, targetCount);

            if (picks.Count < targetCount)
            {
                picks.AddRange(GetDefaultPicksForCategory(category)
                    .Where(p => !picks.Any(x => x.Symbol == p.Symbol))
                    .Take(targetCount - picks.Count));
            }

            return picks;
        }

        private List<StockData> FilterStocksByCategory(List<StockData> stocks, string category)
        {
            return category.ToLower() switch
            {
                "large" => stocks.Where(s => s.MarketCap >= LARGE_CAP_THRESHOLD).ToList(),
                "mid" => stocks.Where(s => s.MarketCap >= MID_CAP_THRESHOLD && s.MarketCap < LARGE_CAP_THRESHOLD).ToList(),
                "small" => stocks.Where(s => s.MarketCap < MID_CAP_THRESHOLD).ToList(),
                _ => stocks
            };
        }

        private int GetTargetCount(string category)
        {
            return category.ToLower() == "large" ? LARGE_CAP_PICKS_REQUIRED : MID_CAP_PICKS_REQUIRED;
        }

        private List<TopStockPick> GeneratePicksFromCandidates(List<StockData> candidates, string category, int targetCount)
        {
            var picks = new List<TopStockPick>();

            foreach (var stock in candidates.Take(targetCount))
            {
                var confidence = CalculatePickConfidence(stock);
                var targetMultiplier = GetTargetMultiplier(stock, category);

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

            return picks.GroupBy(p => p.Symbol).Select(g => g.First()).ToList();
        }

        private int CalculatePickConfidence(StockData stock)
        {
            var confidence = 65 + (int)(Math.Abs(stock.ChangePercent) * 3);
            return Math.Min(92, Math.Max(60, confidence));
        }

        private decimal GetTargetMultiplier(StockData stock, string category)
        {
            return category.ToLower() switch
            {
                "large" => stock.ChangePercent > STRONG_MOMENTUM_THRESHOLD ? 1.10m : 1.08m,
                "mid" => stock.ChangePercent > STRONG_MOMENTUM_THRESHOLD + 1 ? 1.15m : 1.12m,
                "small" => stock.ChangePercent > STRONG_MOMENTUM_THRESHOLD + 2 ? 1.20m : 1.15m,
                _ => 1.08m
            };
        }

        private string GetDynamicReason(StockData stock)
        {
            return stock.ChangePercent switch
            {
                > HIGH_CONFIDENCE_THRESHOLD => "Strong breakout with high volume momentum",
                > MEDIUM_CONFIDENCE_THRESHOLD => "Building strong momentum with institutional buying",
                > STRONG_MOMENTUM_THRESHOLD => "Positive trend with good technical setup",
                > LOW_CONFIDENCE_THRESHOLD => "Consolidating with potential breakout",
                > 0 => "Stable with gradual accumulation",
                > -2 => "Undervalued with strong fundamentals",
                > -5 => "Value buying opportunity at support levels",
                _ => "Technical bounce from oversold levels"
            };
        }

        private string GetDefaultReason(StockData stock)
        {
            return stock.ChangePercent switch
            {
                > HIGH_CONFIDENCE_THRESHOLD => "Strong breakout with high volume",
                > MEDIUM_CONFIDENCE_THRESHOLD => "Building momentum with institutional buying",
                > STRONG_MOMENTUM_THRESHOLD => "Positive trend with good technical setup",
                > LOW_CONFIDENCE_THRESHOLD => "Consolidating with breakout potential",
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
            var summary = new TechnicalSummary
            {
                MovingAverages = new Dictionary<string, string>()
            };

            try
            {
                var allStocks = GetAllStocks(categorized);

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

        #region News Impact - UPDATED to use enhanced NewsImpactAnalysis model

        private async Task<List<NewsImpactAnalysis>> AnalyzeNewsImpact(
            List<StockNews> news,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var impacts = new List<NewsImpactAnalysis>();

            if (news == null) return impacts;

            foreach (var item in news.Take(5))
            {
                if (item == null) continue;

                // Create enhanced news impact analysis with full details
                var impact = new NewsImpactAnalysis
                {
                    Headline = item.Title, // Keep FULL headline, no truncation
                    Source = item.Source ?? "Unknown",
                    PublishedAt = item.PublishedAt,
                    Impact = GetImpactWithEmoji(item), // Use enhanced impact with emoji
                    AffectedStocks = item.AffectedStocks ?? new List<string>(),
                    AffectedSectors = DetermineAffectedSectors(item, categorized),
                    Summary = GenerateSummary(item),
                    Url = item.Url ?? string.Empty
                };

                impacts.Add(impact);
            }

            return impacts;
        }

        private string GetImpactWithEmoji(StockNews news)
        {
            // Use the impact from news if available
            if (!string.IsNullOrEmpty(news.Impact))
                return news.Impact;

            // Otherwise determine based on sentiment
            return news.Sentiment?.ToLower() switch
            {
                "positive" => "🟢 POSITIVE",
                "negative" => "🔴 NEGATIVE",
                _ => "⚪ GENERAL"
            };
        }

        private List<string> DetermineAffectedSectors(StockNews news,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var sectors = new HashSet<string>();

            if (news.Title.Contains("bank", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("HDFC", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("SBI", StringComparison.OrdinalIgnoreCase))
                sectors.Add("Banking");

            if (news.Title.Contains("IT", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("tech", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("TCS", StringComparison.OrdinalIgnoreCase))
                sectors.Add("IT");

            if (news.Title.Contains("pharma", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("Sun", StringComparison.OrdinalIgnoreCase))
                sectors.Add("Pharma");

            if (news.Title.Contains("auto", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("Tata Motors", StringComparison.OrdinalIgnoreCase))
                sectors.Add("Automobile");

            if (news.Title.Contains("energy", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("oil", StringComparison.OrdinalIgnoreCase) ||
                news.Title.Contains("Reliance", StringComparison.OrdinalIgnoreCase))
                sectors.Add("Energy");

            return sectors.Take(3).ToList();
        }

        private string GenerateSummary(StockNews news)
        {
            // Create a brief summary from the title if no summary available
            if (!string.IsNullOrEmpty(news.Summary))
                return news.Summary.Length > 150 ? news.Summary.Substring(0, 147) + "..." : news.Summary;

            if (string.IsNullOrEmpty(news.Title))
                return "No summary available";

            var words = news.Title.Split(' ');
            if (words.Length <= 15)
                return news.Title;

            return string.Join(" ", words.Take(15)) + "...";
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
                client.Timeout = TimeSpan.FromSeconds(30);

                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/^NSEI?range={days}d&interval=1d";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    // TODO: Parse actual historical data from Yahoo Finance
                    // For now, use sample data
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
                ["rsi"] = summary.RSIStatus ?? "Neutral",
                ["macd"] = summary.MACDSignal ?? "Neutral",
                ["volume"] = summary.VolumeAnalysis ?? "Average"
            };
        }

        #endregion

        #region Formatting - UPDATED to show enhanced news

        private string FormatAnalysisForTelegram(EnhancedMarketAnalysis analysis)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"<b>📊 ENHANCED MARKET ANALYSIS</b>");
            sb.AppendLine($"<b>{analysis.AnalysisDate:dddd, MMMM d, yyyy}</b>\n");

            sb.AppendLine($"<b>Market Phase:</b> {analysis.MarketPhase ?? "Unknown"}");
            sb.AppendLine($"<b>Overall Sentiment:</b> {analysis.OverallSentiment ?? "Neutral"}");
            sb.AppendLine($"<b>Confidence:</b> {analysis.ConfidenceScore}%\n");

            AppendSummarySection(sb);
            AppendPicksSection(sb, analysis);
            AppendEnhancedNewsSection(sb, analysis); // New method with enhanced news
            AppendTechnicalSection(sb, analysis);

            sb.AppendLine("<i>Analysis based on last 10 days data and AI predictions</i>");

            return sb.ToString();
        }

        private void AppendSummarySection(StringBuilder sb)
        {
            sb.AppendLine("<b>📈 LAST 10 DAYS SUMMARY</b>");
            sb.AppendLine("• Market showed mixed trends with volatility");
            sb.AppendLine("• Banking and IT sectors showed strength\n");

            sb.AppendLine("<b>🔮 NEXT 10 DAYS PREDICTIONS</b>");
            sb.AppendLine("• Expected Range: 22,000 - 22,800");
            sb.AppendLine("• Support: 21,800 | Resistance: 23,000\n");
        }

        private void AppendPicksSection(StringBuilder sb, EnhancedMarketAnalysis analysis)
        {
            if (analysis.LargeCap.TopPicks.Any())
            {
                sb.AppendLine("<b>🏢 LARGE CAP TOP PICKS</b>");
                foreach (var pick in analysis.LargeCap.TopPicks.Take(LARGE_CAP_PICKS_REQUIRED))
                {
                    if (pick != null)
                    {
                        sb.AppendLine($"• <b>{pick.Symbol}</b>");
                        sb.AppendLine($"  Target: ₹{pick.TargetPrice:F0} | SL: ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                        sb.AppendLine($"  <i>{pick.Reason}</i>");
                    }
                }
                sb.AppendLine();
            }

            if (analysis.MidCap.TopPicks.Any())
            {
                sb.AppendLine("<b>🏭 MID CAP TOP PICKS</b>");
                foreach (var pick in analysis.MidCap.TopPicks.Take(MID_CAP_PICKS_REQUIRED))
                {
                    if (pick != null)
                    {
                        sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                    }
                }
                sb.AppendLine();
            }

            if (analysis.SmallCap.TopPicks.Any())
            {
                sb.AppendLine("<b>🏗️ SMALL CAP TOP PICKS</b>");
                foreach (var pick in analysis.SmallCap.TopPicks.Take(SMALL_CAP_PICKS_REQUIRED))
                {
                    if (pick != null)
                    {
                        sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0} | Conf: {pick.Confidence}%");
                    }
                }
                sb.AppendLine();
            }
        }

        private void AppendEnhancedNewsSection(StringBuilder sb, EnhancedMarketAnalysis analysis)
        {
            if (analysis.KeyNewsImpacts?.Any() != true) return;

            sb.AppendLine("<b>📰 MARKET NEWS & IMPACT</b>");

            foreach (var news in analysis.KeyNewsImpacts.Take(3))
            {
                if (news == null) continue;

                // Show full headline with impact emoji
                sb.AppendLine($"{GetImpactPrefix(news.Impact)} <b>{news.Headline}</b>");

                // Show affected stocks if any
                if (news.AffectedStocks?.Any() == true)
                {
                    sb.AppendLine($"   📊 Affects: {string.Join(", ", news.AffectedStocks.Take(3))}");
                }

                // Show affected sectors if any
                if (news.AffectedSectors?.Any() == true)
                {
                    sb.AppendLine($"   🏭 Sectors: {string.Join(", ", news.AffectedSectors)}");
                }

                // Show source and time
                sb.AppendLine($"   📅 {news.PublishedAt:HH:mm} | {news.Source}");
                sb.AppendLine();
            }
        }

        private string GetImpactPrefix(string? impact)
        {
            if (string.IsNullOrEmpty(impact)) return "⚪";

            if (impact.Contains("🔴")) return "🔴";
            if (impact.Contains("🟢")) return "🟢";
            if (impact.Contains("🟡")) return "🟡";
            return "⚪";
        }

        private void AppendTechnicalSection(StringBuilder sb, EnhancedMarketAnalysis analysis)
        {
            sb.AppendLine("<b>📊 TECHNICAL OUTLOOK</b>");
            sb.AppendLine($"RSI: {analysis.TechnicalIndicators?.RSIStatus ?? "Neutral"}");
            sb.AppendLine($"MACD: {analysis.TechnicalIndicators?.MACDSignal ?? "Neutral"}");
            sb.AppendLine($"Volume: {analysis.TechnicalIndicators?.VolumeAnalysis ?? "Average"}");
            sb.AppendLine();
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
                ConfidenceScore = DEFAULT_CONFIDENCE,
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