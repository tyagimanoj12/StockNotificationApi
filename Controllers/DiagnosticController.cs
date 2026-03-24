using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DiagnosticController : ControllerBase
    {
        private readonly ILogger<DiagnosticController> _logger;

        public DiagnosticController(ILogger<DiagnosticController> logger)
        {
            _logger = logger;
        }

        [HttpGet("test-all")]
        public async Task<IActionResult> TestAllEndpoints()
        {
            var results = new Dictionary<string, object>();

            // Test 1: Simple ping
            results["ping"] = "ok";

            // Test 2: Database connection (if any)
            // Add your DB test here

            // Test 3: External API calls
            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync("https://www.nseindia.com");
                results["nse_api"] = response.IsSuccessStatusCode ? "ok" : $"failed: {response.StatusCode}";
            }
            catch (Exception ex)
            {
                results["nse_api"] = $"error: {ex.Message}";
            }

            // Test 4: Service availability
            var serviceTypes = new[]
            {
            typeof(IStockService),
            typeof(IAngelOneService),
            typeof(ITelegramBotService),
            typeof(IAIService)
        };

            foreach (var type in serviceTypes)
            {
                var service = HttpContext.RequestServices.GetService(type);
                results[type.Name] = service != null ? "registered" : "not found";
            }

            _logger.LogInformation("Diagnostic test completed with results: {@Results}", results);

            return Ok(new
            {
                message = "Diagnostic completed",
                timestamp = DateTime.Now,
                results = results,
                logs_location = @"C:\Inetpub\vhosts\adamyatechnologies.com\stockapi.adamyatechnologies.com\logs"
            });
        }
    }
}
