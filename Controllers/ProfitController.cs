// Controllers/ProfitController.cs
using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

[ApiController]
[Route("api/[controller]")]
public class ProfitController : ControllerBase
{
    private readonly IAngelOneService _angelOneService;
    private readonly IPortfolioService _portfolioService;
    private readonly ILogger<ProfitController> _logger;
    private readonly IAngelOneChatService _angelOneChatService;

    public ProfitController(
        IAngelOneService angelOneService,
        IPortfolioService portfolioService,
        ILogger<ProfitController> logger,
        IAngelOneChatService angelOneChatService    )
    {
        _angelOneService = angelOneService;
        _portfolioService = portfolioService;
        _logger = logger;
        _angelOneChatService = angelOneChatService;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetProfitSummary()
    {
        //var hold = await _angelOneChatService.GetHoldingsAsync();

        var holdings = await _angelOneService.GetHoldingsAsync();

        var summary = new
        {
            totalInvestment = holdings.Sum(h => h.Quantity * h.AveragePrice),
            currentValue = holdings.Sum(h => h.Quantity * h.CurrentPrice),
            totalProfitLoss = holdings.Sum(h => h.ProfitLoss),
            profitLossPercent = holdings.Sum(h => h.Quantity * h.CurrentPrice) > 0
                ? (holdings.Sum(h => h.ProfitLoss) / holdings.Sum(h => h.Quantity * h.AveragePrice)) * 100
                : 0,
            holdings = holdings.Select(h => new
            {
                h.Symbol,
                h.Quantity,
                buyPrice = h.AveragePrice,
                currentPrice = h.CurrentPrice,
                profitLoss = h.ProfitLoss,
                profitLossPercent = ((h.CurrentPrice - h.AveragePrice) / h.AveragePrice) * 100
            })
        };

        return Ok(summary);
    }

    [HttpGet("daily-pnl")]
    public async Task<IActionResult> GetDailyPnL()
    {
        // In production, track this in database
        var todayPnL = new
        {
            date = DateTime.Today,
            realizedPnL = 0, // Track from trades
            unrealizedPnL = 0, // Current holdings P&L
            totalPnL = 0
        };

        return Ok(todayPnL);
    }
}