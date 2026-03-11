using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Controllers // Added namespace
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProfitController : ControllerBase
    {
        private readonly IAngelOneService _angelOneService;
        private readonly IPortfolioService _portfolioService;
        private readonly ILogger<ProfitController> _logger;

        public ProfitController(
            IAngelOneService angelOneService,
            IPortfolioService portfolioService,
            ILogger<ProfitController> logger)
        {
            _angelOneService = angelOneService ?? throw new ArgumentNullException(nameof(angelOneService));
            _portfolioService = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets profit summary from Angel One holdings
        /// </summary>
        /// <returns>Profit summary with holdings details</returns>
        [HttpGet("summary")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetProfitSummary()
        {
            try
            {
                _logger.LogInformation("Fetching profit summary from Angel One");

                var holdings = await _angelOneService.GetHoldingsAsync();

                if (holdings == null || !holdings.Any())
                {
                    _logger.LogInformation("No holdings found");
                    return Ok(new
                    {
                        totalInvestment = 0,
                        currentValue = 0,
                        totalProfitLoss = 0,
                        profitLossPercent = 0,
                        holdings = new List<object>()
                    });
                }

                var totalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice);
                var currentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice);
                var totalProfitLoss = holdings.Sum(h => h.ProfitLoss);

                var summary = new
                {
                    totalInvestment = Math.Round(totalInvestment, 2),
                    currentValue = Math.Round(currentValue, 2),
                    totalProfitLoss = Math.Round(totalProfitLoss, 2),
                    profitLossPercent = totalInvestment > 0
                        ? Math.Round((totalProfitLoss / totalInvestment) * 100, 2)
                        : 0,
                    holdings = holdings.Select(h => new
                    {
                        h.Symbol,
                        h.Quantity,
                        buyPrice = Math.Round(h.AveragePrice, 2),
                        currentPrice = Math.Round(h.CurrentPrice, 2),
                        profitLoss = Math.Round(h.ProfitLoss, 2),
                        profitLossPercent = h.AveragePrice > 0
                            ? Math.Round(((h.CurrentPrice - h.AveragePrice) / h.AveragePrice) * 100, 2)
                            : 0
                    })
                };

                _logger.LogInformation("Profit summary fetched successfully. Total P&L: ₹{TotalProfitLoss}", totalProfitLoss);
                return Ok(summary);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized access to Angel One");
                return Unauthorized(new { error = "Not authenticated with Angel One. Please check your credentials." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching profit summary from Angel One");
                return StatusCode(500, new { error = "Failed to fetch profit summary. Please try again later." });
            }
        }

        /// <summary>
        /// Gets daily profit and loss (realized and unrealized)
        /// </summary>
        /// <returns>Daily P&L breakdown</returns>
        [HttpGet("daily-pnl")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetDailyPnL()
        {
            try
            {
                _logger.LogInformation("Fetching daily P&L");

                var holdings = await _angelOneService.GetHoldingsAsync();

                // Calculate unrealized P&L from current holdings
                var unrealizedPnL = holdings?.Sum(h => h.ProfitLoss) ?? 0;

                // In production, you would track realized P&L from a database or trade history
                // For now, we'll calculate a simple estimate based on holdings changes
                var realizedPnL = await CalculateRealizedPnL();

                var todayPnL = new
                {
                    date = DateTime.Today,
                    realizedPnL = Math.Round(realizedPnL, 2),
                    unrealizedPnL = Math.Round(unrealizedPnL, 2),
                    totalPnL = Math.Round(realizedPnL + unrealizedPnL, 2),
                    asOf = DateTime.Now,
                    holdingsCount = holdings?.Count ?? 0
                };

                _logger.LogInformation("Daily P&L fetched successfully. Total: ₹{TotalPnL}", todayPnL.totalPnL);
                return Ok(todayPnL);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized access to Angel One");
                return Unauthorized(new { error = "Not authenticated with Angel One. Please check your credentials." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching daily P&L");
                return StatusCode(500, new { error = "Failed to fetch daily P&L. Please try again later." });
            }
        }

        /// <summary>
        /// Gets detailed profit breakdown by symbol
        /// </summary>
        /// <returns>Detailed profit analysis</returns>
        [HttpGet("details")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetProfitDetails()
        {
            try
            {
                _logger.LogInformation("Fetching detailed profit breakdown");

                var holdings = await _angelOneService.GetHoldingsAsync();

                if (holdings == null || !holdings.Any())
                {
                    return Ok(new
                    {
                        totalProfit = 0,
                        totalLoss = 0,
                        netProfit = 0,
                        profitableStocks = 0,
                        lossMakingStocks = 0,
                        details = new List<object>()
                    });
                }

                var profitable = holdings.Where(h => h.ProfitLoss > 0).ToList();
                var lossMaking = holdings.Where(h => h.ProfitLoss < 0).ToList();

                var details = new
                {
                    totalProfit = Math.Round(profitable.Sum(h => h.ProfitLoss), 2),
                    totalLoss = Math.Round(Math.Abs(lossMaking.Sum(h => h.ProfitLoss)), 2),
                    netProfit = Math.Round(holdings.Sum(h => h.ProfitLoss), 2),
                    profitableStocks = profitable.Count,
                    lossMakingStocks = lossMaking.Count,
                    bestPerformer = profitable.OrderByDescending(h => h.ProfitLossPercent).FirstOrDefault()?.Symbol ?? "N/A",
                    worstPerformer = lossMaking.OrderBy(h => h.ProfitLossPercent).FirstOrDefault()?.Symbol ?? "N/A",
                    details = holdings.Select(h => new
                    {
                        h.Symbol,
                        h.Quantity,
                        investment = Math.Round(h.Quantity * h.AveragePrice, 2),
                        currentValue = Math.Round(h.Quantity * h.CurrentPrice, 2),
                        profitLoss = Math.Round(h.ProfitLoss, 2),
                        profitLossPercent = Math.Round(h.ProfitLossPercent, 2),
                        status = h.ProfitLoss > 0 ? "PROFIT" : h.ProfitLoss < 0 ? "LOSS" : "BREAK-EVEN"
                    }).OrderByDescending(h => Math.Abs(h.profitLoss))
                };

                return Ok(details);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized access to Angel One");
                return Unauthorized(new { error = "Not authenticated with Angel One" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching profit details");
                return StatusCode(500, new { error = "Failed to fetch profit details" });
            }
        }

        /// <summary>
        /// Gets portfolio performance metrics
        /// </summary>
        /// <returns>Portfolio performance analysis</returns>
        [HttpGet("performance")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPortfolioPerformance()
        {
            try
            {
                _logger.LogInformation("Fetching portfolio performance");

                var holdings = await _angelOneService.GetHoldingsAsync();

                if (holdings == null || !holdings.Any())
                {
                    return Ok(new
                    {
                        totalValue = 0,
                        totalInvestment = 0,
                        overallReturn = 0,
                        overallReturnPercent = 0,
                        metrics = new { }
                    });
                }

                var totalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice);
                var currentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice);
                var totalProfitLoss = currentValue - totalInvestment;
                var profitLossPercent = totalInvestment > 0 ? (totalProfitLoss / totalInvestment) * 100 : 0;

                // Calculate some basic metrics
                var weightedAverageReturn = holdings
                    .Where(h => h.AveragePrice > 0)
                    .Average(h => ((h.CurrentPrice - h.AveragePrice) / h.AveragePrice) * 100);

                var performance = new
                {
                    summary = new
                    {
                        totalValue = Math.Round(currentValue, 2),
                        totalInvestment = Math.Round(totalInvestment, 2),
                        totalProfitLoss = Math.Round(totalProfitLoss, 2),
                        profitLossPercent = Math.Round(profitLossPercent, 2),
                        holdingsCount = holdings.Count
                    },
                    metrics = new
                    {
                        averageReturnPerStock = Math.Round(weightedAverageReturn, 2),
                        profitableStocksRatio = Math.Round((double)holdings.Count(h => h.ProfitLoss > 0) / holdings.Count * 100, 2),
                        largestPosition = holdings.OrderByDescending(h => h.Quantity * h.CurrentPrice).FirstOrDefault()?.Symbol,
                        largestGainer = holdings.OrderByDescending(h => h.ProfitLossPercent).FirstOrDefault()?.Symbol,
                        largestLoser = holdings.OrderByDescending(h => h.ProfitLossPercent).LastOrDefault()?.Symbol
                    }
                };

                return Ok(performance);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized access to Angel One");
                return Unauthorized(new { error = "Not authenticated with Angel One" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching portfolio performance");
                return StatusCode(500, new { error = "Failed to fetch portfolio performance" });
            }
        }

        private async Task<decimal> CalculateRealizedPnL()
        {
            // This is a placeholder. In production, you would:
            // 1. Query a database of closed trades
            // 2. Sum up profits/losses from completed trades today
            // 3. Return the actual realized P&L

            // For now, return 0 as placeholder
            return await Task.FromResult(0m);
        }
    }
}