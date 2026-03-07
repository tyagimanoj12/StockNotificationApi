using Quartz;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add HTTP client factory FIRST
builder.Services.AddHttpClient();

// Configure HttpClients with named clients if needed
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

// Register the CONCRETE StockService FIRST
builder.Services.AddScoped<StockService>();

// Register IStockService interface with the concrete implementation
builder.Services.AddScoped<IStockService, StockService>();

// NOW apply decoration (only use ONE of these approaches)

// OPTION A: Using Scrutor (if you have Scrutor installed)
builder.Services.Decorate<IStockService, FallbackStockService>();

// OPTION B: Manual factory (comment out OPTION A if using this)
// builder.Services.AddScoped<IStockService>(serviceProvider =>
// {
//     var primaryService = serviceProvider.GetRequiredService<StockService>();
//     var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
//     var configuration = serviceProvider.GetRequiredService<IConfiguration>();
//     var logger = serviceProvider.GetRequiredService<ILogger<FallbackStockService>>();
//
//     return new FallbackStockService(
//         primaryService,
//         httpClientFactory,
//         configuration,
//         logger);
// });

// Register AI service - CHOOSE ONLY ONE
builder.Services.AddScoped<IAIService, EnhancedAIService>(); // Using Enhanced version
// builder.Services.AddScoped<IAIService, GeminiAIService>(); // OR original Gemini

// Register notification service
builder.Services.AddScoped<INotificationService, EmailNotificationService>();

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