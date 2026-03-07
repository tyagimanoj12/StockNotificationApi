using Quartz;
using StockNotificationApi.Models;
using StockNotificationApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure HttpClient
builder.Services.AddHttpClient<IStockService, StockService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddHttpClient<IAIService, GeminiAIService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Register services
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IAIService, GeminiAIService>();
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