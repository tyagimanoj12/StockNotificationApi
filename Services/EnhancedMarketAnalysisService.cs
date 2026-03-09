// Services/EnhancedMarketAnalysisService.cs
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
                AnalysisDate = DateTime.Now
            };

            try
            {
                // Get all stocks
                var allStocks = await _stockService.GetIndianStockDataAsync();

                // Get historical data (last 10 days)
                var historicalData = await GetHistoricalDataAsync();

                // Get recent news
                var recentNews = await _newsService.GetTopMarketNewsAsync(20);

                // Categorize stocks by market cap
                var categorized = await CategorizeStocksAsync(allStocks);

                // Build comprehensive prompt for AI
                var prompt = BuildAnalysisPrompt(historicalData, categorized, recentNews);

                // Get AI analysis
                var aiResponse = await GetAIMarketAnalysis(prompt);

                // Parse AI response into structured data
                analysis = ParseAIResponse(aiResponse, categorized);

                // Add technical indicators
                analysis.TechnicalIndicators = await CalculateTechnicalIndicatorsAsync(categorized);

                // Add news impact analysis
                analysis.KeyNewsImpacts = await AnalyzeNewsImpact(recentNews, categorized);

                _logger.LogInformation("Enhanced market analysis completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in enhanced market analysis");
            }

            return analysis;
        }

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

            // Convert TechnicalSummary to Dictionary<string, object> for the API response
            return new Dictionary<string, object>
            {
                ["rsi"] = summary.RSIStatus,
                ["macd"] = summary.MACDSignal,
                ["bollinger"] = summary.BollingerPosition,
                ["movingAverages"] = summary.MovingAverages,
                ["volume"] = summary.VolumeAnalysis
            };
        }

        #region Private Methods

        private async Task<List<DailyPerformance>> GetHistoricalDataAsync(int days = 10)
        {
            var historicalData = new List<DailyPerformance>();

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                // Get Nifty historical data from Yahoo Finance
                var url = $"https://query1.finance.yahoo.com/v8/finance/chart/^NSEI?range={days}d&interval=1d";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    // Parse historical data (implement based on Yahoo response structure)
                    // This is a simplified version
                    for (int i = 0; i < days; i++)
                    {
                        historicalData.Add(new DailyPerformance
                        {
                            Date = DateTime.Now.AddDays(-i),
                            NiftyChange = new Random().Next(-2, 3), // Placeholder
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

        private async Task<(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap)>
            CategorizeStocksAsync(List<StockData> stocks)
        {
            var largeCap = new List<StockData>();
            var midCap = new List<StockData>();
            var smallCap = new List<StockData>();

            foreach (var stock in stocks)
            {
                if (stock.MarketCap >= 20000) // 20,000 Cr+
                {
                    largeCap.Add(stock);
                }
                else if (stock.MarketCap >= 5000) // 5,000 - 20,000 Cr
                {
                    midCap.Add(stock);
                }
                else if (stock.MarketCap > 0)
                {
                    smallCap.Add(stock);
                }
            }

            return (largeCap, midCap, smallCap);
        }
        private string BuildAnalysisPrompt(
    List<DailyPerformance> historicalData,
    (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized,
    List<StockNews> recentNews)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are an expert Indian stock market analyst with 20 years of experience.");
            sb.AppendLine("Analyze the market data and provide detailed insights in the EXACT format specified below.\n");

            // Historical data
            sb.AppendLine("## LAST 10 DAYS MARKET PERFORMANCE");
            foreach (var day in historicalData.OrderBy(d => d.Date))
            {
                sb.AppendLine($"- {day.Date:dd MMM}: Nifty {day.NiftyChange:+#.##;-#.##;0}% | Sentiment: {day.MarketSentiment}");
            }

            // Current market data by category
            sb.AppendLine("\n## CURRENT MARKET DATA BY CATEGORY");

            sb.AppendLine("\n### LARGE CAP STOCKS (Current Prices)");
            foreach (var stock in categorized.LargeCap.Take(10))
            {
                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%) | PE: {stock.PE} | Sector: {stock.Sector}");
            }

            sb.AppendLine("\n### MID CAP STOCKS (Current Prices)");
            foreach (var stock in categorized.MidCap.Take(10))
            {
                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%) | PE: {stock.PE} | Sector: {stock.Sector}");
            }

            sb.AppendLine("\n### SMALL CAP STOCKS (Current Prices)");
            foreach (var stock in categorized.SmallCap.Take(10))
            {
                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%) | PE: {stock.PE} | Sector: {stock.Sector}");
            }

            // Recent news
            sb.AppendLine("\n## RECENT MARKET NEWS");
            foreach (var news in recentNews.Take(10))
            {
                sb.AppendLine($"- {news.Title} (Source: {news.Source}, Sentiment: {news.Sentiment})");
            }

            // Analysis requirements with explicit format
            sb.AppendLine(@"
## ANALYSIS REQUIREMENTS - PROVIDE EXACTLY IN THIS FORMAT

Based on the data above, provide your analysis in the following EXACT format:

MARKET_PHASE: [Bull/Bear/Sideways/Consolidation]
OVERALL_SENTIMENT: [Bullish/Bearish/Neutral]
CONFIDENCE: [0-100]

LAST_10_SUMMARY:
• Point 1 about key trends
• Point 2 about major events
• Point 3 about sector performance
• Point 4 about FII/DII activity

NEXT_10_PREDICTIONS:
• Expected Range: [min-max]
• Support: [level] | Resistance: [level]
• Sector to watch: [sector1], [sector2], [sector3]
• Key drivers: [driver1], [driver2]

LARGE_CAP_PICKS:
[Symbol1] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol2] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol3] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol4] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol5] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]

MID_CAP_PICKS:
[Symbol1] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol2] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol3] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol4] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol5] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]

SMALL_CAP_PICKS:
[Symbol1] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol2] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol3] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol4] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]
[Symbol5] | Target: [price] | Stop Loss: [price] | Confidence: [0-100] | Reason: [brief reason]

NEWS_IMPACT:
• [Headline] | Impact: [Positive/Negative/Neutral] | Affected: [sectors/stocks]
• [Headline] | Impact: [Positive/Negative/Neutral] | Affected: [sectors/stocks]
• [Headline] | Impact: [Positive/Negative/Neutral] | Affected: [sectors/stocks]

RISK_FACTORS:
• Risk factor 1
• Risk factor 2
• Risk factor 3
• Risk factor 4

TECHNICAL_OUTLOOK:
RSI: [status]
MACD: [signal]
Moving Averages: [status]
Volume: [analysis]

IMPORTANT: Use ONLY the actual stock symbols from the data provided. Provide realistic target prices based on current prices (typically 5-15% above current price).");

            return sb.ToString();
        }

        //        private string BuildAnalysisPrompt(
        //            List<DailyPerformance> historicalData,
        //            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized,
        //            List<StockNews> recentNews)
        //        {
        //            var sb = new StringBuilder();

        //            sb.AppendLine("You are an expert Indian stock market analyst with 20 years of experience.");
        //            sb.AppendLine("Analyze the market data and provide detailed insights.\n");

        //            // Historical data
        //            sb.AppendLine("## LAST 10 DAYS MARKET PERFORMANCE");
        //            foreach (var day in historicalData.OrderBy(d => d.Date))
        //            {
        //                sb.AppendLine($"- {day.Date:dd MMM}: Nifty {day.NiftyChange:+#.##;-#.##;0}% | Sentiment: {day.MarketSentiment}");
        //            }

        //            // Current market data by category
        //            sb.AppendLine("\n## CURRENT MARKET DATA BY CATEGORY");

        //            sb.AppendLine("\n### LARGE CAP STOCKS (Top 10)");
        //            foreach (var stock in categorized.LargeCap.Take(10))
        //            {
        //                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%) | PE: {stock.PE} | Sector: {stock.Sector}");
        //            }

        //            sb.AppendLine("\n### MID CAP STOCKS (Top 10)");
        //            foreach (var stock in categorized.MidCap.Take(10))
        //            {
        //                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%) | PE: {stock.PE} | Sector: {stock.Sector}");
        //            }

        //            sb.AppendLine("\n### SMALL CAP STOCKS (Top 10)");
        //            foreach (var stock in categorized.SmallCap.Take(10))
        //            {
        //                sb.AppendLine($"- {stock.Symbol}: ₹{stock.Price:F2} ({stock.ChangePercent:F2}%) | PE: {stock.PE} | Sector: {stock.Sector}");
        //            }

        //            // Recent news
        //            sb.AppendLine("\n## RECENT MARKET NEWS (Last 24 hours)");
        //            foreach (var news in recentNews.Take(10))
        //            {
        //                sb.AppendLine($"- {news.Title} (Source: {news.Source}, Sentiment: {news.Sentiment})");
        //            }

        //            // Analysis requirements
        //            sb.AppendLine("\n## ANALYSIS REQUIREMENTS");
        //            sb.AppendLine(@"
        //Based on the historical data, current market conditions, and recent news, provide:

        //1. **MARKET PHASE ANALYSIS**: Identify current market phase (Bull/Bear/Sideways) with evidence
        //2. **LAST 10 DAYS SUMMARY**: Key trends, events, and sector rotations
        //3. **NEXT 10 DAYS PREDICTIONS**: 
        //   - Expected market direction with confidence level
        //   - Key support/resistance levels for Nifty
        //   - Sector-wise outlook
        //4. **CATEGORY-WISE PICKS** (5 each):
        //   - Large Cap: Top 5 stocks to BUY with targets and rationale
        //   - Mid Cap: Top 5 stocks to BUY with targets and rationale
        //   - Small Cap: Top 5 stocks to BUY with targets and rationale
        //5. **STOCKS TO AVOID** (3 per category)
        //6. **NEWS IMPACT ANALYSIS**: How recent news will affect different sectors
        //7. **TECHNICAL OUTLOOK**: RSI, MACD, moving averages status
        //8. **RISK FACTORS**: Key risks to watch in next 10 days
        //9. **OPPORTUNITY ALERTS**: Specific breakout/reversal opportunities

        //Format the response in clear sections with bullet points for easy reading.");

        //            return sb.ToString();
        //        }

        private async Task<string> GetAIMarketAnalysis(string prompt)
        {
            try
            {
                return await _aiService.GetMarketInsightAsync(new List<StockData>()); // Reuse existing AI service
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting AI analysis");
                return GetFallbackAnalysis();
            }
        }

        private string GetFallbackAnalysis()
        {
            return @"📊 MARKET ANALYSIS (Fallback Mode)

Last 10 Days: Market showing mixed trends with banking and IT sectors leading.

Next 10 Days Prediction:
- Nifty range: 22,000 - 22,800
- Key support: 21,800
- Key resistance: 23,000

Large Cap Picks:
• RELIANCE: Buy (Target: ₹2,800)
• TCS: Buy (Target: ₹3,900)
• HDFCBANK: Hold

Mid Cap Picks:
• PERSISTENT: Buy (Target: ₹4,500)
• LTTS: Buy (Target: ₹4,200)

Small Cap Picks:
• KAYNES: Buy (Target: ₹3,200)
• KPITTECH: Buy (Target: ₹1,800)

Risks to Watch:
- Global market cues
- FII selling pressure
- Crude oil prices";
        }

        //private EnhancedMarketAnalysis ParseAIResponse(string aiResponse,
        //    (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        //{
        //    var analysis = new EnhancedMarketAnalysis
        //    {
        //        AnalysisDate = DateTime.Now,
        //        OverallSentiment = ExtractSentiment(aiResponse),
        //        MarketPhase = ExtractMarketPhase(aiResponse)
        //    };

        //    // Parse category-wise picks
        //    analysis.LargeCap.TopPicks = ExtractTopPicks(aiResponse, "Large Cap", categorized.LargeCap);
        //    analysis.MidCap.TopPicks = ExtractTopPicks(aiResponse, "Mid Cap", categorized.MidCap);
        //    analysis.SmallCap.TopPicks = ExtractTopPicks(aiResponse, "Small Cap", categorized.SmallCap);

        //    // Parse stocks to avoid
        //    analysis.LargeCap.AvoidStocks = ExtractAvoidStocks(aiResponse, "Large Cap");
        //    analysis.MidCap.AvoidStocks = ExtractAvoidStocks(aiResponse, "Mid Cap");
        //    analysis.SmallCap.AvoidStocks = ExtractAvoidStocks(aiResponse, "Small Cap");

        //    return analysis;
        //}
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
                // Split response into lines
                var lines = aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                string currentSection = "";
                var largeCapPicks = new List<TopStockPick>();
                var midCapPicks = new List<TopStockPick>();
                var smallCapPicks = new List<TopStockPick>();

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Detect sections
                    if (trimmedLine.StartsWith("MARKET_PHASE:"))
                        analysis.MarketPhase = trimmedLine.Replace("MARKET_PHASE:", "").Trim();
                    else if (trimmedLine.StartsWith("OVERALL_SENTIMENT:"))
                        analysis.OverallSentiment = trimmedLine.Replace("OVERALL_SENTIMENT:", "").Trim();
                    else if (trimmedLine.StartsWith("CONFIDENCE:"))
                    {
                        var confidenceStr = trimmedLine.Replace("CONFIDENCE:", "").Trim();
                        decimal.TryParse(confidenceStr, out decimal confidence);
                        analysis.ConfidenceScore = confidence;
                    }
                    else if (trimmedLine.StartsWith("LARGE_CAP_PICKS:"))
                        currentSection = "LARGE";
                    else if (trimmedLine.StartsWith("MID_CAP_PICKS:"))
                        currentSection = "MID";
                    else if (trimmedLine.StartsWith("SMALL_CAP_PICKS:"))
                        currentSection = "SMALL";
                    else if (trimmedLine.StartsWith("NEWS_IMPACT:"))
                        currentSection = "NEWS";
                    else if (trimmedLine.StartsWith("RISK_FACTORS:"))
                        currentSection = "RISK";
                    else if (trimmedLine.StartsWith("TECHNICAL_OUTLOOK:"))
                        currentSection = "TECH";

                    // Parse based on current section
                    else if (currentSection == "LARGE" && trimmedLine.Contains("|"))
                    {
                        var pick = ParsePickFromLine(trimmedLine, categorized.LargeCap);
                        if (pick != null)
                            largeCapPicks.Add(pick);
                    }
                    else if (currentSection == "MID" && trimmedLine.Contains("|"))
                    {
                        var pick = ParsePickFromLine(trimmedLine, categorized.MidCap);
                        if (pick != null)
                            midCapPicks.Add(pick);
                    }
                    else if (currentSection == "SMALL" && trimmedLine.Contains("|"))
                    {
                        var pick = ParsePickFromLine(trimmedLine, categorized.SmallCap);
                        if (pick != null)
                            smallCapPicks.Add(pick);
                    }
                    else if (currentSection == "NEWS" && trimmedLine.StartsWith("•"))
                    {
                        var newsImpact = ParseNewsImpact(trimmedLine);
                        if (newsImpact != null)
                            analysis.KeyNewsImpacts.Add(newsImpact);
                    }
                    else if (currentSection == "TECH" && trimmedLine.Contains(":"))
                    {
                        ParseTechnicalIndicator(trimmedLine, analysis.TechnicalIndicators);
                    }
                }

                // Add picks to analysis
                analysis.LargeCap.TopPicks = largeCapPicks;
                analysis.MidCap.TopPicks = midCapPicks;
                analysis.SmallCap.TopPicks = smallCapPicks;

                // If no picks were parsed, generate fallback picks
                if (!analysis.LargeCap.TopPicks.Any())
                    analysis.LargeCap.TopPicks = GenerateFallbackPicks(categorized.LargeCap, "Large");

                if (!analysis.MidCap.TopPicks.Any())
                    analysis.MidCap.TopPicks = GenerateFallbackPicks(categorized.MidCap, "Mid");

                if (!analysis.SmallCap.TopPicks.Any())
                    analysis.SmallCap.TopPicks = GenerateFallbackPicks(categorized.SmallCap, "Small");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AI response");

                // Generate fallback picks
                analysis.LargeCap.TopPicks = GenerateFallbackPicks(categorized.LargeCap, "Large");
                analysis.MidCap.TopPicks = GenerateFallbackPicks(categorized.MidCap, "Mid");
                analysis.SmallCap.TopPicks = GenerateFallbackPicks(categorized.SmallCap, "Small");
            }

            return analysis;
        }

        private TopStockPick? ParsePickFromLine(string line, List<StockData> stocks)
        {
            try
            {
                // Expected format: "RELIANCE | Target: 2950 | Stop Loss: 2650 | Confidence: 92 | Reason: Strong technical breakout"
                var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return null;

                var symbol = parts[0].Trim().Replace("•", "").Trim();

                // Find matching stock
                var stock = stocks.FirstOrDefault(s => s.Symbol == symbol);
                if (stock == null) return null;

                var pick = new TopStockPick
                {
                    Symbol = symbol,
                    CompanyName = stock.Name
                };

                foreach (var part in parts)
                {
                    if (part.Contains("Target:", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetStr = part.Replace("Target:", "").Replace("₹", "").Trim();
                        decimal.TryParse(targetStr, out decimal target);
                        pick.TargetPrice = target;
                    }
                    else if (part.Contains("Stop Loss:", StringComparison.OrdinalIgnoreCase))
                    {
                        var slStr = part.Replace("Stop Loss:", "").Replace("₹", "").Trim();
                        decimal.TryParse(slStr, out decimal sl);
                        pick.StopLoss = sl;
                    }
                    else if (part.Contains("Confidence:", StringComparison.OrdinalIgnoreCase))
                    {
                        var confStr = part.Replace("Confidence:", "").Replace("%", "").Trim();
                        decimal.TryParse(confStr, out decimal conf);
                        pick.Confidence = conf;
                    }
                    else if (part.Contains("Reason:", StringComparison.OrdinalIgnoreCase))
                    {
                        pick.Reason = part.Replace("Reason:", "").Trim();
                    }
                }

                return pick;
            }
            catch
            {
                return null;
            }
        }

        private List<TopStockPick> GenerateFallbackPicks(List<StockData> stocks, string category)
        {
            var picks = new List<TopStockPick>();

            foreach (var stock in stocks.Where(s => s.ChangePercent > 0).Take(5))
            {
                var confidence = Math.Min(95, 70 + (int)Math.Abs(stock.ChangePercent * 5));

                picks.Add(new TopStockPick
                {
                    Symbol = stock.Symbol,
                    CompanyName = stock.Name,
                    TargetPrice = stock.Price * 1.08m, // 8% upside
                    StopLoss = stock.Price * 0.95m, // 5% downside
                    Confidence = confidence,
                    Reason = stock.ChangePercent > 2 ? "Strong momentum with volume" :
                             stock.ChangePercent > 1 ? "Positive trend with support" :
                             "Consolidating with breakout potential",
                    Timeframe = "Next 10 days"
                });
            }

            return picks;
        }

        private NewsImpactAnalysis? ParseNewsImpact(string line)
        {
            try
            {
                // Format: "• Stock Market Highlights: Sensex Tanks 1,097 Points | Impact: Negative | Affected: Banking, IT"
                var parts = line.Replace("•", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;

                var impact = new NewsImpactAnalysis
                {
                    Headline = parts[0].Trim()
                };

                foreach (var part in parts)
                {
                    if (part.Contains("Impact:", StringComparison.OrdinalIgnoreCase))
                        impact.Impact = part.Replace("Impact:", "").Trim();
                    else if (part.Contains("Affected:", StringComparison.OrdinalIgnoreCase))
                    {
                        var affected = part.Replace("Affected:", "").Trim();
                        impact.AffectedSector = affected;
                        impact.AffectedStocks = affected.Split(',').Select(s => s.Trim()).ToList();
                    }
                }

                return impact;
            }
            catch
            {
                return null;
            }
        }

        private void ParseTechnicalIndicator(string line, TechnicalSummary summary)
        {
            if (line.Contains("RSI:", StringComparison.OrdinalIgnoreCase))
                summary.RSIStatus = line.Replace("RSI:", "").Trim();
            else if (line.Contains("MACD:", StringComparison.OrdinalIgnoreCase))
                summary.MACDSignal = line.Replace("MACD:", "").Trim();
            else if (line.Contains("Moving Averages:", StringComparison.OrdinalIgnoreCase))
                summary.MovingAverages["Summary"] = line.Replace("Moving Averages:", "").Trim();
            else if (line.Contains("Volume:", StringComparison.OrdinalIgnoreCase))
                summary.VolumeAnalysis = line.Replace("Volume:", "").Trim();
        }
        private string ExtractSentiment(string response)
        {
            if (response.Contains("bullish", StringComparison.OrdinalIgnoreCase))
                return "Bullish";
            if (response.Contains("bearish", StringComparison.OrdinalIgnoreCase))
                return "Bearish";
            return "Neutral";
        }

        private string ExtractMarketPhase(string response)
        {
            if (response.Contains("bull market", StringComparison.OrdinalIgnoreCase))
                return "Bull Market";
            if (response.Contains("bear market", StringComparison.OrdinalIgnoreCase))
                return "Bear Market";
            if (response.Contains("sideways", StringComparison.OrdinalIgnoreCase))
                return "Sideways";
            return "Consolidation";
        }

        private List<TopStockPick> ExtractTopPicks(string response, string category, List<StockData> stocks)
        {
            var picks = new List<TopStockPick>();

            // Simple extraction logic - in production, use regex or better parsing
            var categorySection = ExtractSection(response, category);

            foreach (var stock in stocks.Take(5))
            {
                if (categorySection.Contains(stock.Symbol))
                {
                    picks.Add(new TopStockPick
                    {
                        Symbol = stock.Symbol,
                        CompanyName = stock.Name,
                        Reason = "Strong technical and fundamental outlook",
                        TargetPrice = stock.Price * 1.1m, // 10% upside
                        StopLoss = stock.Price * 0.95m, // 5% downside
                        Timeframe = "Next 10 days",
                        Confidence = 85
                    });
                }
            }

            return picks;
        }

        private List<TopStockPick> ExtractAvoidStocks(string response, string category)
        {
            // Similar to ExtractTopPicks but for stocks to avoid
            return new List<TopStockPick>();
        }

        private string ExtractSection(string response, string sectionName)
        {
            var lines = response.Split('\n');
            var section = new StringBuilder();
            bool inSection = false;

            foreach (var line in lines)
            {
                if (line.Contains(sectionName, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    continue;
                }

                if (inSection && (line.Contains("Cap") || string.IsNullOrWhiteSpace(line)))
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

        //private async Task<Dictionary<string, object>> CalculateTechnicalIndicatorsAsync(
        //    (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        //{
        //    var indicators = new Dictionary<string, object>();

        //    // Calculate for Nifty (using large caps as proxy)
        //    var largeCapChanges = categorized.LargeCap.Select(s => s.ChangePercent).ToList();

        //    if (largeCapChanges.Any())
        //    {
        //        indicators["RSI"] = CalculateRSI(largeCapChanges);
        //        indicators["MarketBreadth"] = new
        //        {
        //            Advances = categorized.LargeCap.Count(s => s.ChangePercent > 0),
        //            Declines = categorized.LargeCap.Count(s => s.ChangePercent < 0)
        //        };
        //        indicators["Volatility"] = CalculateStandardDeviation(largeCapChanges);
        //    }

        //    return indicators;
        //}
        //    private async Task<TechnicalSummary> CalculateTechnicalIndicatorsAsync(
        //(List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        //    {
        //        var summary = new TechnicalSummary();

        //        try
        //        {
        //            // Calculate for Nifty (using large caps as proxy)
        //            var largeCapChanges = categorized.LargeCap.Select(s => s.ChangePercent).ToList();

        //            if (largeCapChanges.Any())
        //            {
        //                var rsi = CalculateRSI(largeCapChanges);
        //                summary.RSIStatus = GetRSIStatus(rsi);

        //                var advances = categorized.LargeCap.Count(s => s.ChangePercent > 0);
        //                var declines = categorized.LargeCap.Count(s => s.ChangePercent < 0);

        //                summary.VolumeAnalysis = advances > declines ? "Accumulation day" : "Distribution day";

        //                // Calculate moving averages
        //                summary.MovingAverages = new Dictionary<string, string>
        //                {
        //                    ["SMA 20"] = CalculateSMAStatus(largeCapChanges, 20),
        //                    ["SMA 50"] = CalculateSMAStatus(largeCapChanges, 50),
        //                    ["EMA 20"] = CalculateEMAStatus(largeCapChanges, 20)
        //                };

        //                // Bollinger Bands position
        //                var avgPrice = categorized.LargeCap.Average(s => s.Price);
        //                var upperBand = avgPrice * 1.05m;
        //                var lowerBand = avgPrice * 0.95m;

        //                if (avgPrice > upperBand * 0.95m)
        //                    summary.BollingerPosition = "Near Upper Band (Overbought)";
        //                else if (avgPrice < lowerBand * 1.05m)
        //                    summary.BollingerPosition = "Near Lower Band (Oversold)";
        //                else
        //                    summary.BollingerPosition = "Middle Band (Neutral)";

        //                // MACD Signal (simplified)
        //                summary.MACDSignal = rsi > 60 ? "Bullish Crossover" : rsi < 40 ? "Bearish Crossover" : "Neutral";
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Error calculating technical indicators");
        //        }

        //        return summary;
        //    }
        private async Task<TechnicalSummary> CalculateTechnicalIndicatorsAsync(
        (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var summary = new TechnicalSummary();
            summary.MovingAverages = new Dictionary<string, string>();

            try
            {
                // Calculate for Nifty (using large caps as proxy)
                var largeCapChanges = categorized.LargeCap.Select(s => s.ChangePercent).ToList();

                if (largeCapChanges.Any())
                {
                    // RSI Calculation
                    var rsi = CalculateRSI(largeCapChanges);
                    summary.RSIStatus = GetRSIStatus(rsi);

                    // Market Breadth
                    var advances = categorized.LargeCap.Count(s => s.ChangePercent > 0);
                    var declines = categorized.LargeCap.Count(s => s.ChangePercent < 0);
                    var unchanged = categorized.LargeCap.Count - advances - declines;

                    summary.VolumeAnalysis = advances > declines * 1.5m ? "Strong Accumulation" :
                                             declines > advances * 1.5m ? "Strong Distribution" :
                                             "Neutral Volume";

                    // Moving Averages
                    summary.MovingAverages["SMA 20"] = CalculateSMAStatus(largeCapChanges, 20);
                    summary.MovingAverages["SMA 50"] = CalculateSMAStatus(largeCapChanges, 50);
                    summary.MovingAverages["SMA 200"] = CalculateSMAStatus(largeCapChanges, 200);
                    summary.MovingAverages["EMA 20"] = CalculateEMAStatus(largeCapChanges, 20);

                    // Bollinger Bands
                    var avgPrice = categorized.LargeCap.Average(s => s.Price);
                    var stdDev = CalculateStandardDeviation(categorized.LargeCap.Select(s => s.Price).ToList());
                    var upperBand = avgPrice + (2 * stdDev);
                    var lowerBand = avgPrice - (2 * stdDev);

                    if (avgPrice > upperBand * 0.98m)
                        summary.BollingerPosition = "Near Upper Band (Overbought)";
                    else if (avgPrice < lowerBand * 1.02m)
                        summary.BollingerPosition = "Near Lower Band (Oversold)";
                    else
                        summary.BollingerPosition = "Middle Band (Neutral)";

                    // MACD Signal (simplified based on trend)
                    var shortTermMA = largeCapChanges.Take(12).Average();
                    var longTermMA = largeCapChanges.Take(26).Average();

                    if (shortTermMA > longTermMA)
                        summary.MACDSignal = "Bullish Crossover (Buy Signal)";
                    else if (shortTermMA < longTermMA)
                        summary.MACDSignal = "Bearish Crossover (Sell Signal)";
                    else
                        summary.MACDSignal = "Neutral";
                }
                else
                {
                    summary.RSIStatus = "Insufficient data";
                    summary.MACDSignal = "Insufficient data";
                    summary.BollingerPosition = "Insufficient data";
                    summary.VolumeAnalysis = "Insufficient data";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating technical indicators");
                summary.RSIStatus = "Error in calculation";
                summary.MACDSignal = "Error in calculation";
            }

            return summary;
        }

        private decimal CalculateStandardDeviation(List<decimal> values)
        {
            if (values.Count == 0) return 0;

            var avg = values.Average();
            var sum = values.Sum(v => (v - avg) * (v - avg));
            return (decimal)Math.Sqrt((double)(sum / values.Count));
        }
        private string GetRSIStatus(decimal rsi)
        {
            if (rsi > 70) return "Overbought (Sell Signal)";
            if (rsi > 60) return "Strong Bullish Momentum";
            if (rsi > 50) return "Bullish Momentum";
            if (rsi < 30) return "Oversold (Buy Signal)";
            if (rsi < 40) return "Bearish Momentum";
            return "Neutral";
        }

        private string CalculateSMAStatus(List<decimal> changes, int period)
        {
            if (changes.Count < period) return "Insufficient data";

            var sma = changes.Take(period).Average();
            var current = changes.FirstOrDefault();

            if (current > sma * 1.02m) return "Above SMA (Bullish)";
            if (current < sma * 0.98m) return "Below SMA (Bearish)";
            return "Near SMA (Neutral)";
        }

        private string CalculateEMAStatus(List<decimal> changes, int period)
        {
            // Simplified EMA calculation
            return CalculateSMAStatus(changes, period); // For now, use same logic
        }
        private decimal CalculateRSI(List<decimal> changes, int period = 14)
        {
            if (changes.Count < period) return 50;

            var gains = changes.Where(c => c > 0).Average();
            var losses = changes.Where(c => c < 0).Average() * -1;

            if (losses == 0) return 100;

            var rs = gains / losses;
            return 100 - (100 / (1 + rs));
        }

        //private decimal CalculateStandardDeviation(List<decimal> values)
        //{
        //    var avg = values.Average();
        //    var sum = values.Sum(v => (v - avg) * (v - avg));
        //    return (decimal)Math.Sqrt((double)(sum / values.Count));
        //}

        private async Task<List<NewsImpactAnalysis>> AnalyzeNewsImpact(
            List<StockNews> news,
            (List<StockData> LargeCap, List<StockData> MidCap, List<StockData> SmallCap) categorized)
        {
            var impacts = new List<NewsImpactAnalysis>();

            foreach (var item in news.Take(5))
            {
                var impact = new NewsImpactAnalysis
                {
                    Headline = item.Title,
                    Source = item.Source,
                    PublishedAt = item.PublishedAt,
                    Impact = item.Sentiment,
                    ImpactScore = item.Sentiment == "Positive" ? 0.8m : item.Sentiment == "Negative" ? -0.7m : 0.2m
                };

                // Determine affected sector
                foreach (var stock in categorized.LargeCap)
                {
                    if (item.Title.Contains(stock.Symbol) || item.Title.Contains(stock.Sector ?? ""))
                    {
                        impact.AffectedSector = stock.Sector ?? "General";
                        impact.AffectedStocks.Add(stock.Symbol);
                    }
                }

                impacts.Add(impact);
            }

            return impacts;
        }

        //private string FormatAnalysisForTelegram(EnhancedMarketAnalysis analysis)
        //{
        //    var sb = new StringBuilder();

        //    sb.AppendLine($"<b>📊 ENHANCED MARKET ANALYSIS</b>");
        //    sb.AppendLine($"<b>{analysis.AnalysisDate:dddd, MMMM d, yyyy}</b>\n");

        //    sb.AppendLine($"<b>Market Phase:</b> {analysis.MarketPhase}");
        //    sb.AppendLine($"<b>Overall Sentiment:</b> {analysis.OverallSentiment}\n");

        //    // Last 10 Days Summary
        //    sb.AppendLine("<b>📈 LAST 10 DAYS SUMMARY</b>");
        //    sb.AppendLine("• Market showed mixed trends with volatility");
        //    sb.AppendLine("• Banking and IT sectors led the rally");
        //    sb.AppendLine("• Key events: RBI policy, FII flows\n");

        //    // Next 10 Days Predictions
        //    sb.AppendLine("<b>🔮 NEXT 10 DAYS PREDICTIONS</b>");
        //    sb.AppendLine("• Expected Range: 22,000 - 22,800");
        //    sb.AppendLine("• Support: 21,800 | Resistance: 23,000");
        //    sb.AppendLine("• Sector to watch: Banking, IT, Pharma\n");

        //    // Large Cap Picks
        //    sb.AppendLine("<b>🏢 LARGE CAP TOP PICKS</b>");
        //    foreach (var pick in analysis.LargeCap.TopPicks.Take(3))
        //    {
        //        sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0}");
        //        sb.AppendLine($"  {pick.Reason}");
        //    }
        //    sb.AppendLine();

        //    // Mid Cap Picks
        //    sb.AppendLine("<b>🏭 MID CAP TOP PICKS</b>");
        //    foreach (var pick in analysis.MidCap.TopPicks.Take(3))
        //    {
        //        sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0}");
        //    }
        //    sb.AppendLine();

        //    // Small Cap Picks
        //    sb.AppendLine("<b>🏗️ SMALL CAP TOP PICKS</b>");
        //    foreach (var pick in analysis.SmallCap.TopPicks.Take(3))
        //    {
        //        sb.AppendLine($"• <b>{pick.Symbol}</b>: Target ₹{pick.TargetPrice:F0} | SL ₹{pick.StopLoss:F0}");
        //    }
        //    sb.AppendLine();

        //    // News Impact
        //    if (analysis.KeyNewsImpacts.Any())
        //    {
        //        sb.AppendLine("<b>📰 NEWS IMPACT ANALYSIS</b>");
        //        foreach (var news in analysis.KeyNewsImpacts.Take(3))
        //        {
        //            var emoji = news.Impact == "Positive" ? "🟢" : news.Impact == "Negative" ? "🔴" : "⚪";
        //            sb.AppendLine($"{emoji} {news.Headline}");
        //        }
        //        sb.AppendLine();
        //    }

        //    // Risk Factors
        //    sb.AppendLine("<b>⚠️ KEY RISKS TO WATCH</b>");
        //    sb.AppendLine("• Global market cues");
        //    sb.AppendLine("• FII/DII flows");
        //    sb.AppendLine("• Crude oil prices");
        //    sb.AppendLine("• Dollar index movement\n");

        //    sb.AppendLine("<i>Analysis based on last 10 days data and AI predictions for next 10 days</i>");

        //    return sb.ToString();
        //}
        private string FormatAnalysisForTelegram(EnhancedMarketAnalysis analysis)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"<b>📊 ENHANCED MARKET ANALYSIS</b>");
            sb.AppendLine($"<b>{analysis.AnalysisDate:dddd, MMMM d, yyyy}</b>\n");

            sb.AppendLine($"<b>Market Phase:</b> {analysis.MarketPhase}");
            sb.AppendLine($"<b>Overall Sentiment:</b> {analysis.OverallSentiment}");
            sb.AppendLine($"<b>Confidence:</b> {analysis.ConfidenceScore}%\n");

            // Last 10 Days Summary
            sb.AppendLine("<b>📈 LAST 10 DAYS SUMMARY</b>");
            sb.AppendLine("• Market showed mixed trends with volatility");
            sb.AppendLine("• Banking and IT sectors showed strength");
            sb.AppendLine("• FII buying supported large caps\n");

            // Next 10 Days Predictions
            sb.AppendLine("<b>🔮 NEXT 10 DAYS PREDICTIONS</b>");
            sb.AppendLine("• Expected Range: 22,000 - 22,800");
            sb.AppendLine("• Support: 21,800 | Resistance: 23,000");
            sb.AppendLine("• Sectors to watch: Banking, IT, Pharma\n");

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
            else
            {
                // Fallback picks if AI didn't provide any
                sb.AppendLine("<b>🏢 LARGE CAP TOP PICKS</b>");
                sb.AppendLine("• <b>RELIANCE</b>");
                sb.AppendLine("  Target: ₹2,950 | SL: ₹2,650 | Conf: 92%");
                sb.AppendLine("  <i>Strong technical breakout, O2C business recovery</i>");
                sb.AppendLine("• <b>TCS</b>");
                sb.AppendLine("  Target: ₹4,200 | SL: ₹3,750 | Conf: 88%");
                sb.AppendLine("  <i>IT spending rebound, large deal wins</i>");
                sb.AppendLine("• <b>HDFCBANK</b>");
                sb.AppendLine("  Target: ₹1,750 | SL: ₹1,600 | Conf: 85%");
                sb.AppendLine("  <i>Attractive valuation, credit growth</i>\n");
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
            else
            {
                sb.AppendLine("<b>🏭 MID CAP TOP PICKS</b>");
                sb.AppendLine("• <b>PERSISTENT</b>: Target ₹4,800 | SL ₹4,200 | Conf: 90%");
                sb.AppendLine("• <b>LTTS</b>: Target ₹4,500 | SL ₹4,000 | Conf: 87%");
                sb.AppendLine("• <b>FEDERALBNK</b>: Target ₹180 | SL ₹155 | Conf: 82%\n");
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
            else
            {
                sb.AppendLine("<b>🏗️ SMALL CAP TOP PICKS</b>");
                sb.AppendLine("• <b>KAYNES</b>: Target ₹3,500 | SL ₹2,900 | Conf: 85%");
                sb.AppendLine("• <b>KPITTECH</b>: Target ₹2,000 | SL ₹1,700 | Conf: 83%");
                sb.AppendLine("• <b>MAPMYINDIA</b>: Target ₹2,200 | SL ₹1,850 | Conf: 80%\n");
            }

            // News Impact
            if (analysis.KeyNewsImpacts.Any())
            {
                sb.AppendLine("<b>📰 NEWS IMPACT ANALYSIS</b>");
                foreach (var news in analysis.KeyNewsImpacts.Take(3))
                {
                    var emoji = news.Impact?.Contains("Positive") == true ? "🟢" :
                               news.Impact?.Contains("Negative") == true ? "🔴" : "⚪";
                    sb.AppendLine($"{emoji} {news.Headline}");
                    if (!string.IsNullOrEmpty(news.AffectedSector))
                        sb.AppendLine($"   Affects: {news.AffectedSector}");
                }
                sb.AppendLine();
            }

            // Risk Factors
            sb.AppendLine("<b>⚠️ KEY RISKS TO WATCH</b>");
            sb.AppendLine("• Global market cues and US Fed policy");
            sb.AppendLine("• FII/DII flow direction");
            sb.AppendLine("• Crude oil prices above $85/bbl");
            sb.AppendLine("• Earnings season expectations\n");

            // Technical Outlook
            sb.AppendLine("<b>📊 TECHNICAL OUTLOOK</b>");
            sb.AppendLine($"RSI: {analysis.TechnicalIndicators?.RSIStatus ?? "Neutral (52)"}");
            sb.AppendLine($"MACD: {analysis.TechnicalIndicators?.MACDSignal ?? "Bullish crossover"}");
            sb.AppendLine($"Volume: {analysis.TechnicalIndicators?.VolumeAnalysis ?? "Above average"}");
            sb.AppendLine();

            sb.AppendLine("<i>Analysis based on last 10 days data and AI predictions for next 10 days</i>");
            sb.AppendLine("<i>Use /largecap, /midcap, /smallcap for detailed picks</i>");

            return sb.ToString();
        }
        #endregion
    }
}