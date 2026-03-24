using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace StockNotificationApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DebugController : ControllerBase
    {
        [HttpGet("logs")]
        public IActionResult ViewLogs()
        {
            var logs = new List<object>();
            var possibleLogs = new[]
            {
            "/tmp/stockapi-debug.log",
            "/tmp/stockapi-requests.log",
            "/tmp/stockapi-errors.log",
            "debug.log",
            Path.Combine(Directory.GetCurrentDirectory(), "logs", "stockapi-.txt")
        };

            foreach (var logPath in possibleLogs)
            {
                try
                {
                    if (System.IO.File.Exists(logPath))
                    {
                        var content = System.IO.File.ReadAllLines(logPath);
                        logs.Add(new
                        {
                            path = logPath,
                            exists = true,
                            size = new FileInfo(logPath).Length,
                            lastModified = new FileInfo(logPath).LastWriteTime,
                            lines = content.TakeLast(50).ToArray() // Last 50 lines
                        });
                    }
                    else
                    {
                        logs.Add(new { path = logPath, exists = false });
                    }
                }
                catch (Exception ex)
                {
                    logs.Add(new { path = logPath, error = ex.Message });
                }
            }

            return Ok(logs);
        }

        [HttpGet("test-error")]
        public IActionResult TestError()
        {
            throw new Exception("This is a test exception to verify logging!");
        }

        [HttpGet("test-log")]
        public IActionResult TestLog()
        {
            var logPath = Path.Combine(Path.GetTempPath(), "stockapi-test.log");
            System.IO.File.WriteAllText(logPath, "Test log entry at " + DateTime.Now);
            return Ok(new { message = "Log written", file = logPath });
        }
    }
}
