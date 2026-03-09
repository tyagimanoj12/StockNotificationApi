using Quartz;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Program.cs - Add these lines

builder.Services.AddSingleton<TelegramBotService>(); // Register concrete type
builder.Services.AddSingleton<ITelegramBotService>(sp =>
    sp.GetRequiredService<TelegramBotService>()); // Register interface
builder.Services.AddHostedService<TelegramBotService>(sp =>
    sp.GetRequiredService<TelegramBotService>()); // Register hosted service

builder.Services.AddScoped<IStockListService, StockListService>();

// Register the CONCRETE StockService
builder.Services.AddScoped<StockService>();

// Register IStockService interface with the concrete implementation
builder.Services.AddScoped<IStockService, StockService>();

// OPTION A: Using Scrutor (if you have Scrutor installed)
// Make sure you have installed Scrutor NuGet package
builder.Services.Decorate<IStockService, FallbackStockService>();

// OPTION B: Manual factory (comment out OPTION A if using this)
/*
builder.Services.AddScoped<IStockService>(serviceProvider =>
{
    var primaryService = serviceProvider.GetRequiredService<StockService>();
    var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var logger = serviceProvider.GetRequiredService<ILogger<FallbackStockService>>();
    var stockListService = serviceProvider.GetRequiredService<IStockListService>(); // Add this if needed

    return new FallbackStockService(
        primaryService,
        httpClientFactory,
        configuration,
        logger);
});
*/

// Register AI service
builder.Services.AddScoped<IAIService, EnhancedAIService>();

// Register notification service
builder.Services.AddScoped<INotificationService, EmailNotificationService>();
// Add these lines to Program.cs
builder.Services.AddScoped<INewsService, NewsService>();
builder.Services.AddScoped<IDailyBriefingService, DailyBriefingService>();
// Program.cs - Add this line with your other service registrations
builder.Services.AddScoped<IPortfolioService, PortfolioService>();
// Program.cs
builder.Services.AddScoped<IEnhancedMarketAnalysisService, EnhancedMarketAnalysisService>();

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
var serviceProvider = app.Services;
await StockPredictionJobScheduler.ScheduleJobs(serviceProvider);

app.Run();