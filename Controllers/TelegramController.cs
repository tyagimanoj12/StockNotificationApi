using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TelegramController : ControllerBase
    {
        private readonly ITelegramBotService _telegramService;
        private readonly IStockService _stockService;
        private readonly ILogger<TelegramController> _logger;

        public TelegramController(
            ITelegramBotService telegramService,
            IStockService stockService,
            ILogger<TelegramController> logger)
        {
            _telegramService = telegramService;
            _stockService = stockService;
            _logger = logger;
        }

        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] string message)
        {
            await _telegramService.BroadcastToAllAsync(message);
            return Ok(new { message = "Broadcast sent" });
        }

        [HttpPost("send-report")]
        public async Task<IActionResult> SendReport()
        {
            var stocks = await _stockService.GetIndianStockDataAsync();
            // Create report and send
            return Ok(new { message = "Report sent" });
        }

        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            // Return bot statistics
            return Ok(new { status = "running" });
        }
    }
}