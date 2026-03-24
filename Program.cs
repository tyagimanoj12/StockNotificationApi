using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;
using Serilog;
using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Rewrite;
using Serilog.Events;
using System.Runtime.InteropServices;
using StockNotificationApi.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ===== EXCEPTION HANDLING =====
AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;

// ===== DEBUG LOGGING SETUP =====
string debugLogPath = SetupDebugLogging();
void WriteDebug(string message) => WriteToDebugLog(debugLogPath, message);

WriteDebug("Application starting...");
WriteDebug($"Process ID: {Environment.ProcessId}");
WriteDebug($"Working Directory: {Directory.GetCurrentDirectory()}");
WriteDebug($"OS Platform: {RuntimeInformation.OSDescription}");

// ===== SERILOG LOGGING SETUP =====
string logDirectory = SetupLogDirectory();
ConfigureSerilog(builder, logDirectory);

// ===== APPLICATION INSIGHTS =====
ConfigureApplicationInsights(builder);

// ===== ADD SERVICES =====
ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();
WriteDebug("Application built successfully");

// ===== REQUEST/ERROR LOGGING MIDDLEWARE =====
app.UseMiddleware<RequestLoggingMiddleware>(debugLogPath, WriteDebug);

// ===== PLESK FIXES =====
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// ===== SWAGGER =====
var rewriteOptions = new RewriteOptions().AddRedirect("^$", "swagger");
app.UseRewriter(rewriteOptions);
app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Stock API V1");
    c.RoutePrefix = "swagger";
});

// ===== MIDDLEWARE =====
app.UseResponseCompression();
app.UseRateLimiter();

if (!app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

// ===== HEALTH CHECKS =====
MapHealthChecks(app);

// ===== API ENDPOINTS =====
app.MapControllers();

// ===== SCHEDULER INITIALIZATION =====
await InitializeScheduler(app, WriteDebug);

// ===== KEEP-ALIVE MECHANISM =====
SetupKeepAlive(app, WriteDebug);

WriteDebug("[OK] Application started successfully");

// ===== GRACEFUL SHUTDOWN =====
try
{
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var shutdownTimeout = TimeSpan.FromSeconds(30);

    lifetime.ApplicationStopping.Register(() =>
    {
        WriteDebug($"[INFO] Application stopping - beginning graceful shutdown (timeout: {shutdownTimeout.TotalSeconds}s)...");

        // Force exit after timeout
        Task.Delay(shutdownTimeout).ContinueWith(_ =>
        {
            WriteDebug("[ERROR] Shutdown timeout reached - forcing exit");
            Environment.Exit(1);
        });
    });

    lifetime.ApplicationStopped.Register(() =>
    {
        WriteDebug("[INFO] Application stopped completely");
    });

    await app.RunAsync(lifetime.ApplicationStopping);

    // Allow time for cleanup
    await Task.Delay(1000, CancellationToken.None);
}
catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
{
    WriteDebug("[INFO] Application shutdown requested gracefully");
}
catch (OperationCanceledException ex)
{
    WriteDebug($"[INFO] Application cancelled: {ex.Message}");
}
catch (Exception ex)
{
    WriteDebug($"[ERROR] Application crashed: {ex}");
    WriteDebug($"Stack trace: {ex.StackTrace}");

    // Log to file
    var crashLog = Path.Combine(Path.GetTempPath(), $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    try
    {
        await File.WriteAllTextAsync(crashLog, $"{DateTime.Now}: {ex}");
        WriteDebug($"[INFO] Crash log saved to: {crashLog}");
    }
    catch { }

    throw;
}
finally
{
    WriteDebug("[INFO] Application shutdown complete");
    Log.CloseAndFlush();
    await Task.Delay(500, CancellationToken.None);
}

// ============================================================================
// HELPER METHODS
// ============================================================================

#region Exception Handling

static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args)
{
    var exception = args.ExceptionObject as Exception;
    var errorMessage = $"FATAL UNHANDLED EXCEPTION: {exception?.Message}\n{exception?.StackTrace}";
    Console.WriteLine(errorMessage);

    var paths = new[] { "/tmp/startup-error.log", "startup-error.log" };
    foreach (var path in paths)
    {
        try { File.WriteAllText(path, errorMessage); } catch { }
    }
}

static void TaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
{
    var exception = e.Exception;
    var errorMessage = $"UNOBSERVED TASK EXCEPTION: {exception.Message}\n{exception.StackTrace}";
    Console.WriteLine(errorMessage);

    try
    {
        File.AppendAllText("/tmp/task-errors.log", $"\n=== {DateTime.Now} ===\n{errorMessage}\n");
    }
    catch { }

    e.SetObserved();
}

#endregion

#region Debug Logging

static string SetupDebugLogging()
{
    string debugLogPath = null;
    var possibleDebugPaths = new[]
    {
        "/tmp/stockapi-debug.log",
        "/var/log/stockapi-debug.log",
        Path.Combine(Directory.GetCurrentDirectory(), "debug.log"),
        "debug.log"
    };

    foreach (var path in possibleDebugPaths)
    {
        try
        {
            File.WriteAllText(path, $"=== DEBUG LOG STARTED at {DateTime.Now} ===\n");
            File.AppendAllText(path, $"Process ID: {Environment.ProcessId}\n");
            File.AppendAllText(path, $"Working Directory: {Directory.GetCurrentDirectory()}\n");
            File.AppendAllText(path, $"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}\n");
            debugLogPath = path;
            Console.WriteLine($"[OK] Debug log: {debugLogPath}");
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Cannot write to {path}: {ex.Message}");
        }
    }
    return debugLogPath;
}

static void WriteToDebugLog(string debugLogPath, string message)
{
    if (debugLogPath != null)
    {
        try
        {
            File.AppendAllText(debugLogPath, $"{DateTime.Now:HH:mm:ss.fff}: {message}\n");
        }
        catch { }
    }
    Console.WriteLine(message);
}

#endregion

#region Log Directory Setup

static string SetupLogDirectory()
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return SetupLinuxLogDirectory();
        }
        else
        {
            return SetupWindowsLogDirectory();
        }
    }
    catch (Exception ex)
    {
        var tempPath = Path.GetTempPath();
        Console.WriteLine($"[WARN] Using temp directory: {tempPath} (Error: {ex.Message})");
        return tempPath;
    }
}

static string SetupLinuxLogDirectory()
{
    var possiblePaths = new[]
    {
        "/var/log/stockapi",
        "/tmp/stockapi-logs",
        Path.Combine(Directory.GetCurrentDirectory(), "logs")
    };

    foreach (var path in possiblePaths)
    {
        try
        {
            Directory.CreateDirectory(path);
            var testFile = Path.Combine(path, $"test-{Guid.NewGuid()}.txt");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            Console.WriteLine($"[OK] Using log directory: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Cannot write to {path}: {ex.Message}");
        }
    }

    return Path.GetTempPath();
}

static string SetupWindowsLogDirectory()
{
    var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
    Directory.CreateDirectory(logDirectory);
    return logDirectory;
}

#endregion

#region Serilog Configuration

static void ConfigureSerilog(WebApplicationBuilder builder, string logDirectory)
{
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("Application", "StockNotificationApi")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logDirectory, "stockapi-.txt"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 14,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1)
            )
            .WriteTo.File(
                path: Path.Combine(Path.GetTempPath(), "stockapi-fatal-.txt"),
                rollingInterval: RollingInterval.Day,
                restrictedToMinimumLevel: LogEventLevel.Error,
                shared: true
            );

        var appInsightsConnectionString = context.Configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrEmpty(appInsightsConnectionString))
        {
            configuration.WriteTo.ApplicationInsights(
                connectionString: appInsightsConnectionString,
                telemetryConverter: TelemetryConverter.Traces);
        }
    });
}

#endregion

#region Application Insights

static void ConfigureApplicationInsights(WebApplicationBuilder builder)
{
    var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrEmpty(appInsightsConnectionString))
    {
        builder.Services.AddApplicationInsightsTelemetry(options =>
        {
            options.ConnectionString = appInsightsConnectionString;
        });
    }
}

#endregion

#region Service Configuration

static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    // Basic services
    services.AddControllers();
    services.AddEndpointsApiExplorer();
    services.AddSwaggerGen();

    // Response Compression
    services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });

    // Rate Limiting
    ConfigureRateLimiting(services);

    // Health Checks
    ConfigureHealthChecks(services);

    // HTTP Client
    services.AddHttpClient();

    // Memory Cache
    services.AddMemoryCache();

    // Hosted Services
    services.AddHostedService<TelegramBotService>();
    services.AddHostedService<PriceAlertService>();
    services.AddHostedService<CommandProcessor>();
    services.AddHostedService<AutoTradingService>();

    // Quartz Scheduler
    services.AddQuartz(q =>
    {
        q.UseMicrosoftDependencyInjectionJobFactory();
        q.UseSimpleTypeLoader();
        q.UseInMemoryStore();
        q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 10);
    });
    services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    // Application services
    services.AddApplicationServices(configuration);

    // Health check for hosted services
    services.AddSingleton<HostedServicesHealthCheck>();
}

static void ConfigureRateLimiting(IServiceCollection services)
{
    services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: partition => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = RateLimitConstants.MAX_COMMANDS_PER_MINUTE * 5,
                    QueueLimit = 5,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    Window = TimeSpan.FromMinutes(RateLimitConstants.RATE_LIMIT_WINDOW_MINUTES)
                }));

        options.AddPolicy("ApiPolicy", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
                factory: partition => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = 50,
                    QueueLimit = 2,
                    Window = TimeSpan.FromMinutes(1)
                }));

        options.AddPolicy("BotPolicy", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: partition => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = 200,
                    QueueLimit = 10,
                    Window = TimeSpan.FromMinutes(1)
                }));

        options.OnRejected = async (context, token) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", token);
        };
    });
}

static void ConfigureHealthChecks(IServiceCollection services)
{
    services.AddHealthChecks()
        .AddUrlGroup(new Uri("https://www.nseindia.com"),
            name: "NSE Website",
            failureStatus: HealthStatus.Degraded,
            timeout: TimeSpan.FromSeconds(5))
        .AddUrlGroup(new Uri("https://finance.yahoo.com"),
            name: "Yahoo Finance",
            failureStatus: HealthStatus.Degraded,
            timeout: TimeSpan.FromSeconds(5))
        .AddUrlGroup(new Uri("https://generativelanguage.googleapis.com"),
            name: "Google Gemini",
            failureStatus: HealthStatus.Degraded,
            timeout: TimeSpan.FromSeconds(5))
        .AddTypeActivatedCheck<AngelOneHealthCheck>("Angel One",
            failureStatus: HealthStatus.Unhealthy,
            args: Array.Empty<object>())
        .AddCheck<HostedServicesHealthCheck>("Hosted Services")
        .AddProcessAllocatedMemoryHealthCheck(
            maximumMegabytesAllocated: HealthCheckConstants.MEMORY_LIMIT_MB,
            name: "Memory Usage",
            failureStatus: HealthStatus.Degraded)
        .AddDiskStorageHealthCheck(
            setup => setup.AddDrive(GetSystemDrive(), minimumFreeMegabytes: HealthCheckConstants.MIN_FREE_DISK_MB),
            name: "Disk Space",
            failureStatus: HealthStatus.Degraded);
}

static string GetSystemDrive() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "C:\\" : "/";

#endregion

#region Health Check Mapping

static void MapHealthChecks(WebApplication app)
{
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        AllowCachingResponses = false,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        }
    });

    app.MapHealthChecks("/health/detailed", new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                status = report.Status.ToString(),
                totalDuration = report.TotalDuration,
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    duration = e.Value.Duration,
                    tags = e.Value.Tags,
                    exception = e.Value.Exception?.Message
                })
            };
            await context.Response.WriteAsJsonAsync(response);
        }
    });
}

#endregion

#region Scheduler Initialization

static async Task InitializeScheduler(WebApplication app, Action<string> writeDebug)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        await StockPredictionJobScheduler.ScheduleJobs(serviceProvider);
        app.Logger.LogInformation("Scheduler initialized");
        writeDebug("[OK] Scheduler initialized");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Scheduler failed");
        writeDebug($"[ERROR] Scheduler failed: {ex}");
    }
}

#endregion

#region Keep-Alive Setup

static void SetupKeepAlive(WebApplication app, Action<string> writeDebug)
{
    writeDebug("[INFO] Setting up keep-alive mechanism");

    var startTime = DateTime.Now;
    CancellationTokenSource? keepAliveCts = null;

    // Bot status endpoint
    app.MapGet("/bot-status", (IServiceProvider services) =>
    {
        try
        {
            var botService = services.GetService<ITelegramBotService>();
            return Results.Ok(new
            {
                status = "alive",
                time = DateTime.Now,
                botServiceExists = botService != null,
                botServiceType = botService?.GetType().Name ?? "null",
                uptime = DateTime.Now - startTime,
                environment = app.Environment.EnvironmentName,
                memory = GC.GetTotalMemory(false) / 1024 / 1024 + " MB"
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new
            {
                status = "degraded",
                error = ex.Message,
                time = DateTime.Now
            });
        }
    });

    // Bot health endpoint
    app.MapGet("/bot-health", () => Results.Ok(new
    {
        status = "healthy",
        time = DateTime.Now,
        message = "Bot endpoint is reachable"
    }));

    // Ping endpoint
    app.MapGet("/ping", () => Results.Ok(new
    {
        pong = DateTime.Now,
        message = "Keep-alive ping received",
        uptime = DateTime.Now - startTime
    }));

    // Start background keep-alive task
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        writeDebug("[INFO] Starting keep-alive background task");
        keepAliveCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            var httpClient = new HttpClient();
            var baseUrl = GetBaseUrl(app.Configuration);
            writeDebug($"[INFO] Keep-alive will ping: {baseUrl}/ping");

            int pingCount = 0;
            var token = keepAliveCts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), token);

                    if (token.IsCancellationRequested)
                        break;

                    pingCount++;
                    writeDebug($"[INFO] Keep-alive ping #{pingCount} at {DateTime.Now:HH:mm:ss}");

                    var response = await httpClient.GetAsync($"{baseUrl}/ping", token);
                    writeDebug($"[OK] Keep-alive response: {(int)response.StatusCode} {response.StatusCode}");

                    if (pingCount % 3 == 0)
                    {
                        var healthResponse = await httpClient.GetAsync($"{baseUrl}/health", token);
                        writeDebug($"[HEALTH] Health check: {(int)healthResponse.StatusCode} {healthResponse.StatusCode}");
                    }

                    if (pingCount % 5 == 0)
                    {
                        var botResponse = await httpClient.GetAsync($"{baseUrl}/bot-status", token);
                        writeDebug($"[BOT] Bot status check: {(int)botResponse.StatusCode} {botResponse.StatusCode}");
                    }
                }
                catch (TaskCanceledException)
                {
                    writeDebug("[INFO] Keep-alive task cancelled");
                    break;
                }
                catch (OperationCanceledException)
                {
                    writeDebug("[INFO] Keep-alive operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    writeDebug($"[ERROR] Keep-alive failed: {ex.Message}");
                }
            }

            httpClient.Dispose();
            writeDebug("[INFO] Keep-alive task stopped");
        });
    });

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        writeDebug("[WARN] Application stopping - stopping keep-alive...");
        keepAliveCts?.Cancel();
        keepAliveCts?.Dispose();
    });
}

static string GetBaseUrl(IConfiguration configuration)
{
    var baseUrl = configuration["Application:BaseUrl"];
    if (!string.IsNullOrEmpty(baseUrl))
        return baseUrl;

    return "https://stockapi.adamyatechnologies.com";
}

#endregion

// ============================================================================
// MIDDLEWARE CLASSES
// ============================================================================

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _debugLogPath;
    private readonly Action<string> _writeDebug;

    public RequestLoggingMiddleware(RequestDelegate next, string debugLogPath, Action<string> writeDebug)
    {
        _next = next;
        _debugLogPath = debugLogPath;
        _writeDebug = writeDebug;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestPath = context.Request.Path;
        var requestMethod = context.Request.Method;

        _writeDebug($"-> Request: {requestMethod} {requestPath}");

        if (requestMethod == "POST" || requestMethod == "PUT")
        {
            context.Request.EnableBuffering();
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            context.Request.Body.Position = 0;
            if (!string.IsNullOrEmpty(body))
                _writeDebug($"  Body: {body}");
        }

        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex, requestPath, _writeDebug);
        }
        finally
        {
            await LogResponseAsync(context, responseBody, originalBodyStream, _writeDebug);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception ex, string requestPath, Action<string> writeDebug)
    {
        writeDebug($"[ERROR] EXCEPTION: {ex.GetType().Name}: {ex.Message}");

        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            var errorResponse = new
            {
                error = "Internal server error",
                type = ex.GetType().Name,
                message = ex.Message,
                path = requestPath,
                timestamp = DateTime.UtcNow
            };
            await context.Response.WriteAsJsonAsync(errorResponse);
        }
    }

    private static async Task LogResponseAsync(HttpContext context, MemoryStream responseBody, Stream originalBodyStream, Action<string> writeDebug)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseText = await new StreamReader(context.Response.Body).ReadToEndAsync();
        context.Response.Body.Seek(0, SeekOrigin.Begin);

        if (context.Response.StatusCode >= 400)
        {
            writeDebug($"<- Response: {context.Response.StatusCode} - {responseText}");
        }

        await responseBody.CopyToAsync(originalBodyStream);
    }
}

// ============================================================================
// HEALTH CHECK CLASSES
// ============================================================================

public class AngelOneHealthCheck : IHealthCheck
{
    private readonly IAngelOneService _angelOneService;
    private readonly ILogger<AngelOneHealthCheck> _logger;

    public AngelOneHealthCheck(IAngelOneService angelOneService, ILogger<AngelOneHealthCheck> logger)
    {
        _angelOneService = angelOneService;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var session = await _angelOneService.AuthenticateAsync();

            if (session != null && !string.IsNullOrEmpty(session.AuthToken))
            {
                return HealthCheckResult.Healthy("Angel One API is reachable and authenticated");
            }

            return HealthCheckResult.Degraded("Angel One authentication failed");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Angel One API timeout");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Angel One API is unreachable", ex);
        }
    }
}

public class HostedServicesHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HostedServicesHealthCheck> _logger;

    public HostedServicesHealthCheck(IServiceProvider serviceProvider, ILogger<HostedServicesHealthCheck> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var services = new List<(string Name, object? Service)>
            {
                ("TelegramBot", _serviceProvider.GetService<TelegramBotService>()),
                ("PriceAlert", _serviceProvider.GetService<PriceAlertService>()),
                ("CommandProcessor", _serviceProvider.GetService<CommandProcessor>()),
                ("AutoTrading", _serviceProvider.GetService<AutoTradingService>())
            };

            var unhealthyServices = new List<string>();

            foreach (var (name, service) in services)
            {
                if (service == null)
                {
                    unhealthyServices.Add($"{name} (not registered)");
                }
            }

            if (unhealthyServices.Any())
            {
                return Task.FromResult(HealthCheckResult.Degraded($"Some services are unhealthy: {string.Join(", ", unhealthyServices)}"));
            }

            return Task.FromResult(HealthCheckResult.Healthy("All hosted services are running"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to check hosted services", ex));
        }
    }
}