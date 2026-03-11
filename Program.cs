using HealthChecks.UI.Client;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Quartz;
using Serilog;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;
using System.Net.Http;
using System.Threading.RateLimiting;
using HealthChecks.Uris;
using HealthChecks.System;

var builder = WebApplication.CreateBuilder(args);

// Get Application Insights connection string from configuration (optional)
var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];

// ===== ADD SERILOG LOGGING =====
builder.Host.UseSerilog((context, config) =>
{
    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .Enrich.WithMachineName()
          .Enrich.WithEnvironmentName()
          .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
          .WriteTo.File(
              path: "logs/stockapi-.txt",
              rollingInterval: RollingInterval.Day,
              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
              retainedFileCountLimit: 7);

    // Only add Application Insights if connection string is provided
    if (!string.IsNullOrEmpty(appInsightsConnectionString))
    {
        config.WriteTo.ApplicationInsights(
            connectionString: appInsightsConnectionString,
            telemetryConverter: TelemetryConverter.Traces);
    }
});

// Add Application Insights telemetry (optional)
if (!string.IsNullOrEmpty(appInsightsConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = appInsightsConnectionString;
    });
}

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ===== ADD RESPONSE COMPRESSION =====
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// ===== ADD RATE LIMITING =====
builder.Services.AddRateLimiter(options =>
{
    // Global limiter - 100 requests per minute per IP
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                QueueLimit = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Specific policy for API endpoints
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

    // Policy for Telegram bot endpoints (higher limits)
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

// ===== ADD HEALTH CHECKS =====
builder.Services.AddHealthChecks()
    .AddUrlGroup(new Uri("https://www.nseindia.com"),
        name: "NSE API",
        failureStatus: HealthStatus.Unhealthy,
        timeout: TimeSpan.FromSeconds(5))
    .AddUrlGroup(new Uri("https://query1.finance.yahoo.com"),
        name: "Yahoo Finance",
        failureStatus: HealthStatus.Unhealthy,
        timeout: TimeSpan.FromSeconds(5))
    .AddUrlGroup(new Uri("https://generativelanguage.googleapis.com"),
        name: "Gemini AI",
        failureStatus: HealthStatus.Unhealthy,
        timeout: TimeSpan.FromSeconds(5))
    .AddTypeActivatedCheck<AngelOneHealthCheck>("Angel One",
        failureStatus: HealthStatus.Unhealthy,
        args: Array.Empty<object>())
    .AddProcessAllocatedMemoryHealthCheck(
        maximumMegabytesAllocated: 500,
        name: "Memory Usage",
        failureStatus: HealthStatus.Degraded)
    .AddDiskStorageHealthCheck(
        setup => setup.AddDrive("C:\\", minimumFreeMegabytes: 1024),
        name: "Disk Space",
        failureStatus: HealthStatus.Degraded);

// Add MemoryCache
builder.Services.AddMemoryCache();

// Register Cache Service
builder.Services.AddSingleton<ICacheService, MemoryCacheService>();

// Add HTTP client factory
builder.Services.AddHttpClient();

// Configure HttpClients with named clients
builder.Services.AddHttpClient("StockClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddHttpClient("GeminiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Register Telegram Bot Service with Price Alert Service
builder.Services.AddSingleton<IPriceAlertService, PriceAlertService>();
builder.Services.AddHostedService(sp => (PriceAlertService)sp.GetRequiredService<IPriceAlertService>());

builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddSingleton<ITelegramBotService>(sp =>
    sp.GetRequiredService<TelegramBotService>());
builder.Services.AddHostedService<TelegramBotService>(sp =>
    sp.GetRequiredService<TelegramBotService>());

// Register Stock List Service
builder.Services.AddScoped<IStockListService, StockListService>();

// Register StockService with ICacheService
builder.Services.AddScoped<IStockService, StockService>();

// Register Fallback Service (Decorator pattern) - if Scrutor is installed
try
{
    builder.Services.Decorate<IStockService, FallbackStockService>();
    Log.Information("FallbackStockService registered via Scrutor");
}
catch (InvalidOperationException)
{
    Log.Warning("Scrutor not installed. Install with: dotnet add package Scrutor");
}

// Register AI service
builder.Services.AddHttpClient<IAIService, GeminiService>();

// Register notification services
builder.Services.AddScoped<INotificationService, EmailNotificationService>();
builder.Services.AddScoped<INewsService, NewsService>();
builder.Services.AddScoped<IDailyBriefingService, DailyBriefingService>();
builder.Services.AddScoped<IPortfolioService, PortfolioService>();
builder.Services.AddScoped<IEnhancedMarketAnalysisService, EnhancedMarketAnalysisService>();
builder.Services.AddScoped<ITradingService, TradingService>();
builder.Services.AddSingleton<IMarketStatusService, MarketStatusService>();

// Register Angel One service
builder.Services.AddHttpClient("AngelOne");
builder.Services.AddScoped<IAngelOneService, AngelOneService>();

// Register HoldingOptimizer
builder.Services.AddScoped<HoldingOptimizer>();

// Register Groww Service Conditionally
var growwApiKey = builder.Configuration["Groww:ApiKey"];
var growwApiSecret = builder.Configuration["Groww:ApiSecret"];
var useGroww = !string.IsNullOrEmpty(growwApiKey) && !string.IsNullOrEmpty(growwApiSecret);

if (useGroww)
{
    builder.Services.AddScoped<IGrowwService, GrowwService>();
    Log.Information("Groww service registered (API key found)");
}
else
{
    builder.Services.AddScoped<IGrowwService, NullGrowwService>();
    Log.Information("Groww service not configured - using null implementation");
}

// Register AutoTradingService
builder.Services.AddSingleton<AutoTradingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoTradingService>());

// Configure Quartz for background jobs
builder.Services.AddQuartz(q =>
{
    q.UseMicrosoftDependencyInjectionJobFactory();

    var jobKey = new JobKey("StockPredictionJob");
    q.AddJob<StockPredictionBackgroundService>(opts => opts.WithIdentity(jobKey));

    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("StockPredictionJob-trigger")
        .WithCronSchedule("0 0 9 * * ?")); // Run at 9:00 AM daily
});

builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

// Configure settings
builder.Services.Configure<NotificationSettings>(
    builder.Configuration.GetSection("NotificationSettings"));

builder.Services.Configure<GeminiAISettings>(
    builder.Configuration.GetSection("GeminiAISettings"));

builder.Services.Configure<StockApiSettings>(
    builder.Configuration.GetSection("StockApiSettings"));

builder.Services.Configure<EmailSettings>(
    builder.Configuration.GetSection("EmailSettings"));

var app = builder.Build();

// ===== ADD SERILOG REQUEST LOGGING =====
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RemoteIp", httpContext.Connection.RemoteIpAddress?.ToString());
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].ToString());
    };
});

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ===== USE RESPONSE COMPRESSION =====
app.UseResponseCompression();

// ===== USE RATE LIMITING =====
app.UseRateLimiter();

app.UseHttpsRedirection();
app.UseAuthorization();

// ===== MAP HEALTH CHECKS =====
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

// Detailed health check for diagnostics
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

app.MapControllers();

// Initialize scheduler
try
{
    using var scope = app.Services.CreateScope();
    var serviceProvider = scope.ServiceProvider;
    StockPredictionJobScheduler.ScheduleJobs(serviceProvider).GetAwaiter().GetResult();
    app.Logger.LogInformation("Stock prediction job scheduler initialized successfully");
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Failed to initialize stock prediction job scheduler");
}

app.Run();

// ===== CUSTOM HEALTH CHECK FOR ANGEL ONE =====
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
            _logger.LogDebug("Running Angel One health check");

            // Create a timeout token
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var session = await _angelOneService.AuthenticateAsync();

            if (session != null && !string.IsNullOrEmpty(session.AuthToken))
            {
                _logger.LogDebug("Angel One health check passed");
                return HealthCheckResult.Healthy("Angel One API is reachable and authenticated");
            }

            _logger.LogWarning("Angel One health check degraded - authentication failed");
            return HealthCheckResult.Degraded("Angel One authentication failed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Angel One health check timed out");
            return HealthCheckResult.Unhealthy("Angel One API timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Angel One health check failed");
            return HealthCheckResult.Unhealthy("Angel One API is unreachable", ex);
        }
    }
}