using Microsoft.Extensions.Options;
using Quartz;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    [DisallowConcurrentExecution]
    public class StockPredictionBackgroundService : IJob
    {
        private readonly IStockService _stockService;
        private readonly IAIService _aiService;
        private readonly INotificationService _notificationService;
        private readonly ILogger<StockPredictionBackgroundService> _logger;
        private readonly NotificationSettings _notificationSettings;

        public StockPredictionBackgroundService(
            IStockService stockService,
            IAIService aiService,
            INotificationService notificationService,
            IOptions<NotificationSettings> notificationSettings,
            ILogger<StockPredictionBackgroundService> logger)
        {
            _stockService = stockService;
            _aiService = aiService;
            _notificationService = notificationService;
            _logger = logger;
            _notificationSettings = notificationSettings.Value;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                _logger.LogInformation("Starting daily stock prediction job");

                // Fetch real-time stock data
                var stockData = await _stockService.GetIndianStockDataAsync();

                if (stockData == null || !stockData.Any())
                {
                    _logger.LogWarning("No stock data retrieved");
                    return;
                }

                _logger.LogInformation("Retrieved data for {Count} stocks", stockData.Count);

                // Generate AI predictions
                var predictions = await _aiService.GeneratePredictionsAsync(stockData);

                // Get market insight
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stockData);

                // Send email notification
                await _notificationService.SendDailyPredictionReport(predictions);

                _logger.LogInformation("Daily stock prediction job completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in daily stock prediction job");
            }
        }
    }

    public class StockPredictionJobScheduler
    {
        public static async Task ScheduleJobs(IServiceProvider serviceProvider)
        {
            var schedulerFactory = serviceProvider.GetRequiredService<ISchedulerFactory>();
            var scheduler = await schedulerFactory.GetScheduler();

            var job = JobBuilder.Create<StockPredictionBackgroundService>()
                .WithIdentity("stockPredictionJob", "group1")
                .Build();

            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var notificationSettings = configuration.GetSection("NotificationSettings").Get<NotificationSettings>();

            // Parse send time
            var sendTime = TimeSpan.Parse(notificationSettings?.SendTime ?? "09:00");

            // Schedule job to run daily at specified time
            var trigger = TriggerBuilder.Create()
                .WithIdentity("stockPredictionTrigger", "group1")
                .StartNow()
                .WithSchedule(CronScheduleBuilder.DailyAtHourAndMinute(sendTime.Hours, sendTime.Minutes))
                .Build();

            await scheduler.ScheduleJob(job, trigger);
        }
    }
}