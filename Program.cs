using Quartz;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add MemoryCache
builder.Services.AddMemoryCache();

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

// Register Telegram Bot Service
builder.Services.AddSingleton<TelegramBotService>(); // Register concrete type
builder.Services.AddSingleton<ITelegramBotService>(sp =>
    sp.GetRequiredService<TelegramBotService>()); // Register interface
builder.Services.AddHostedService<TelegramBotService>(sp =>
    sp.GetRequiredService<TelegramBotService>()); // Register hosted service

// Register Stock List Service
builder.Services.AddScoped<IStockListService, StockListService>();

// Register the CONCRETE StockService
builder.Services.AddScoped<StockService>();

// Register IStockService interface with the concrete implementation
builder.Services.AddScoped<IStockService, StockService>();

// Register Fallback Service (Decorator pattern)
// Note: Make sure you have installed Scrutor NuGet package for this to work
// dotnet add package Scrutor
builder.Services.Decorate<IStockService, FallbackStockService>();

// Register AI service
builder.Services.AddScoped<IAIService, EnhancedAIService>();

// Register notification services
builder.Services.AddScoped<INotificationService, EmailNotificationService>();
builder.Services.AddScoped<INewsService, NewsService>();
builder.Services.AddScoped<IDailyBriefingService, DailyBriefingService>();
builder.Services.AddScoped<IPortfolioService, PortfolioService>();
builder.Services.AddScoped<IEnhancedMarketAnalysisService, EnhancedMarketAnalysisService>();
builder.Services.AddScoped<ITradingService, TradingService>();
builder.Services.AddHttpClient("AngelOne");
builder.Services.AddScoped<IAngelOneService, AngelOneService>();
builder.Services.AddScoped<IAngelOneChatService, AngelOneChatService>();

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

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Initialize scheduler
try
{
    var serviceProvider = app.Services;
    await StockPredictionJobScheduler.ScheduleJobs(serviceProvider);
    app.Logger.LogInformation("Stock prediction job scheduler initialized successfully");
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Failed to initialize stock prediction job scheduler");
}

app.Run();