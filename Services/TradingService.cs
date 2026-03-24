using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class TradingService : ITradingService
    {
        private readonly IEnhancedMarketAnalysisService _analysisService;
        private readonly IStockService _stockService;
        private readonly ILogger<TradingService> _logger;
        private readonly ICacheService _cache; // Added

        // Use constants from Constants.cs
        private const decimal MAX_REALISTIC_RETURN_PERCENT = TradingConstants.MAX_REALISTIC_RETURN_PERCENT;
        private const decimal MIN_STOP_LOSS_DISTANCE = TradingConstants.MIN_STOP_LOSS_DISTANCE;
        private const decimal MAX_STOP_LOSS_DISTANCE = TradingConstants.MAX_STOP_LOSS_DISTANCE;

        // Cache constants
        private const int TRADES_CACHE_MINUTES = CacheConstants.TRADES_CACHE_MINUTES;
        private const int TOP_TRADES_CACHE_MINUTES = CacheConstants.TOP_TRADES_CACHE_MINUTES;
        private const int CATEGORY_TRADES_CACHE_MINUTES = CacheConstants.CATEGORY_TRADES_CACHE_MINUTES;

        // Confidence
        private const int MIN_CONFIDENCE = TradingConstants.MIN_CONFIDENCE;

        public TradingService(
            IEnhancedMarketAnalysisService analysisService,
            IStockService stockService,
            ILogger<TradingService> logger,
            ICacheService cache) // Added
        {
            _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task<TradingDashboard> GetTodaysTradesAsync(int minConfidence = 70)
        {
            // Use cache for trading dashboard - different cache key based on confidence
            var cacheKey = $"trading_dashboard_{minConfidence}";

            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                _logger.LogInformation("Cache miss for today's trades with min confidence {MinConfidence}", minConfidence);

                var analysis = await _analysisService.AnalyzeMarketAsync();

                var allTrades = new List<TradeItem>();

                // Process each category
                allTrades.AddRange(await ProcessCategory(analysis.LargeCap.TopPicks, "Large Cap", minConfidence));
                allTrades.AddRange(await ProcessCategory(analysis.MidCap.TopPicks, "Mid Cap", minConfidence));
                allTrades.AddRange(await ProcessCategory(analysis.SmallCap.TopPicks, "Small Cap", minConfidence));

                // Sort by Risk/Reward first (best returns relative to risk), then by Confidence
                allTrades = allTrades
                    .Where(t => t != null && IsValidTrade(t))
                    .OrderByDescending(t => t.RiskReward)     // 🔥 Best risk/reward first
                    .ThenByDescending(t => t.Confidence)      // 🔥 Then highest confidence
                    .ToList();

                var dashboard = new TradingDashboard
                {
                    MarketPhase = analysis.MarketPhase,
                    MarketConfidence = analysis.ConfidenceScore,
                    Trades = allTrades,
                    Summary = new TradingSummary
                    {
                        TotalTrades = allTrades.Count,
                        HighConfidenceTrades = allTrades.Count(p => p.Confidence >= 80),
                        MediumConfidenceTrades = allTrades.Count(p => p.Confidence >= 70 && p.Confidence < 80),
                        AverageConfidence = allTrades.Any() ? allTrades.Average(p => p.Confidence) : 0,
                        BestRiskReward = allTrades.Any() ? allTrades.Max(p => p.RiskReward) : 0,
                        TotalPotentialReturn = allTrades.Any() ? allTrades.Average(p => p.PotentialReturn) : 0
                    }
                };

                return dashboard;
            }, TimeSpan.FromMinutes(TRADES_CACHE_MINUTES));
        }

        public async Task<List<TradeItem>> GetTopTradesAsync(int count = 5)
        {
            // Use cache for top trades - different cache key based on count
            var cacheKey = $"top_trades_{count}";

            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                _logger.LogInformation("Cache miss for top {Count} trades", count);

                var dashboard = await GetTodaysTradesAsync(60); // Lower threshold to get more picks

                if (dashboard?.Trades == null || !dashboard.Trades.Any())
                {
                    return new List<TradeItem>();
                }

                // Return top trades by risk/reward and confidence
                return dashboard.Trades
                    .Where(t => t != null && t.TargetPrice > t.CurrentPrice && t.StopLoss < t.CurrentPrice)
                    .OrderByDescending(t => t.RiskReward)      // 🔥 Sort by risk/reward first
                    .ThenByDescending(t => t.Confidence)       // 🔥 Then by confidence
                    .Take(count)
                    .ToList();
            }, TimeSpan.FromMinutes(TOP_TRADES_CACHE_MINUTES));
        }

        public async Task<List<TradeItem>> GetTradesByCategoryAsync(string category, int minConfidence = 70)
        {
            // Use cache for category trades
            var cacheKey = $"trades_{category}_{minConfidence}";

            return await _cache.GetOrSetAsync(cacheKey, async () =>
            {
                _logger.LogInformation("Cache miss for {Category} trades with min confidence {MinConfidence}", category, minConfidence);

                var analysis = await _analysisService.AnalyzeMarketAsync();

                List<TopStockPick> picks = category.ToLower() switch
                {
                    "large" => analysis.LargeCap.TopPicks,
                    "mid" => analysis.MidCap.TopPicks,
                    "small" => analysis.SmallCap.TopPicks,
                    _ => new List<TopStockPick>()
                };

                var trades = new List<TradeItem>();
                foreach (var pick in picks.Where(p => p.Confidence >= minConfidence))
                {
                    var trade = await MapToTradeItemWithCurrentPrice(pick, category);
                    if (trade != null && IsValidTrade(trade))
                        trades.Add(trade);
                }

                return trades
                    .OrderByDescending(t => t.RiskReward)      // 🔥 Sort by risk/reward first
                    .ThenByDescending(t => t.Confidence)       // 🔥 Then by confidence
                    .ToList();
            }, TimeSpan.FromMinutes(CATEGORY_TRADES_CACHE_MINUTES));
        }

        #region Private Helper Methods

        private async Task<List<TradeItem>> ProcessCategory(List<TopStockPick> picks, string category, int minConfidence)
        {
            var trades = new List<TradeItem>();
            foreach (var pick in picks.Where(p => p.Confidence >= minConfidence))
            {
                var trade = await MapToTradeItemWithCurrentPrice(pick, category);
                if (trade != null && IsValidTrade(trade))
                {
                    trades.Add(trade);
                }
                else
                {
                    _logger.LogDebug("Filtered out {Symbol} - Invalid trade parameters", pick?.Symbol);
                }
            }
            return trades;
        }

        private bool IsValidTrade(TradeItem trade)
        {
            if (trade == null) return false;

            // Check 1: Stop loss must be BELOW current price
            if (trade.StopLoss >= trade.CurrentPrice)
            {
                _logger.LogDebug("Invalid trade {Symbol}: Stop loss {SL} >= Current {Current}",
                    trade.Symbol, trade.StopLoss, trade.CurrentPrice);
                return false;
            }

            // Check 2: Target must be ABOVE current price
            if (trade.TargetPrice <= trade.CurrentPrice)
            {
                _logger.LogDebug("Invalid trade {Symbol}: Target {Target} <= Current {Current}",
                    trade.Symbol, trade.TargetPrice, trade.CurrentPrice);
                return false;
            }

            // Check 3: Stop loss distance should be reasonable (5-15%)
            var stopLossPercent = ((trade.CurrentPrice - trade.StopLoss) / trade.CurrentPrice) * 100;
            if (stopLossPercent < MIN_STOP_LOSS_DISTANCE || stopLossPercent > MAX_STOP_LOSS_DISTANCE)
            {
                _logger.LogDebug("Invalid trade {Symbol}: Stop loss distance {Distance:F1}% outside reasonable range",
                    trade.Symbol, stopLossPercent);
                return false;
            }

            // Check 4: Cap unrealistic returns
            if (trade.PotentialReturn > MAX_REALISTIC_RETURN_PERCENT)
            {
                _logger.LogDebug("Unrealistic return for {Symbol}: {Return:F1}% capped to {Max}%",
                    trade.Symbol, trade.PotentialReturn, MAX_REALISTIC_RETURN_PERCENT);
                trade.PotentialReturn = MAX_REALISTIC_RETURN_PERCENT;
                // Still return true but with capped return
            }

            // Check 5: Risk/Reward should be at least 1:1 for high confidence trades
            if (trade.Confidence >= 80 && trade.RiskReward < 1.0m)
            {
                _logger.LogDebug("Poor risk/reward for high confidence {Symbol}: {RR}:1",
                    trade.Symbol, trade.RiskReward);
                // Still include but log warning
            }

            return true;
        }

        private async Task<TradeItem?> MapToTradeItemWithCurrentPrice(TopStockPick pick, string category)
        {
            try
            {
                if (pick == null) return null;

                // Get current price from stock service
                var stockData = await _stockService.GetStockDataAsync($"{pick.Symbol}.NS");
                if (stockData == null)
                {
                    stockData = await _stockService.GetStockDataAsync($"{pick.Symbol}.BO");
                }

                if (stockData == null)
                {
                    _logger.LogWarning("Could not fetch current price for {Symbol}", pick.Symbol);
                    return null;
                }

                decimal currentPrice = stockData.Price;

                // CRITICAL FIX: For KPITTECH and MAPMYINDIA, the stop loss is above current price
                // This indicates these might be sell signals, not buy signals
                if (pick.StopLoss >= currentPrice)
                {
                    _logger.LogInformation("Skipping {Symbol} - Stop loss {SL} is above current price {Current} (likely a sell signal)",
                        pick.Symbol, pick.StopLoss, currentPrice);
                    return null;
                }

                // Calculate risk/reward properly
                decimal riskReward = CalculateRiskReward(currentPrice, pick.TargetPrice, pick.StopLoss);

                // If risk/reward is 0 or negative, skip
                if (riskReward <= 0)
                {
                    _logger.LogDebug("Skipping {Symbol} - Invalid risk/reward: {RR}", pick.Symbol, riskReward);
                    return null;
                }

                var potentialReturn = CalculateReturn(currentPrice, pick.TargetPrice);

                return new TradeItem
                {
                    Symbol = pick.Symbol,
                    Category = category,
                    CurrentPrice = currentPrice,
                    TargetPrice = pick.TargetPrice,
                    StopLoss = pick.StopLoss,
                    Confidence = pick.Confidence,
                    RiskReward = riskReward,
                    PotentialReturn = Math.Min(potentialReturn, MAX_REALISTIC_RETURN_PERCENT), // Cap at 50%
                    Reason = pick.Reason
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing {Symbol}", pick?.Symbol);
                return null;
            }
        }

        private decimal CalculateRiskReward(decimal entry, decimal target, decimal stopLoss)
        {
            // Ensure stop loss is below entry
            if (stopLoss >= entry) return 0;

            var potentialProfit = target - entry;
            var potentialLoss = entry - stopLoss;

            if (potentialLoss <= 0) return 0;

            return Math.Round(potentialProfit / potentialLoss, 2);
        }

        private decimal CalculateReturn(decimal entry, decimal target)
        {
            if (entry <= 0) return 0;
            return Math.Round(((target - entry) / entry) * 100, 2);
        }

        #endregion
    }
}