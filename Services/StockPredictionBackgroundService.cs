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

        // Constants
        private const string JOB_IDENTITY = "stockPredictionJob";
        private const string JOB_GROUP = "group1";
        private const string TRIGGER_IDENTITY = "stockPredictionTrigger";
        private const string DEFAULT_SEND_TIME = "09:00";
        private const string TIME_PARSE_ERROR = "Invalid time format";

        public StockPredictionBackgroundService(
            IStockService stockService,
            IAIService aiService,
            INotificationService notificationService,
            IOptions<NotificationSettings> notificationSettings,
            ILogger<StockPredictionBackgroundService> logger)
        {
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _notificationSettings = notificationSettings?.Value ?? throw new ArgumentNullException(nameof(notificationSettings));
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var jobStartTime = DateTime.UtcNow;

            try
            {
                _logger.LogInformation("Starting daily stock prediction job at {Time}", jobStartTime);

                // Fetch real-time stock data
                var stockData = await _stockService.GetIndianStockDataAsync();

                if (stockData == null || !stockData.Any())
                {
                    _logger.LogWarning("No stock data retrieved during job execution");
                    return;
                }

                _logger.LogInformation("Retrieved data for {Count} stocks successfully", stockData.Count);

                // Generate AI predictions
                _logger.LogDebug("Generating AI predictions for {Count} stocks", stockData.Count);
                var predictions = await _aiService.GeneratePredictionsAsync(stockData);

                if (predictions == null)
                {
                    _logger.LogError("Failed to generate AI predictions");
                    return;
                }

                // Get market insight
                _logger.LogDebug("Fetching market insight");
                predictions.MarketSummary = await _aiService.GetMarketInsightAsync(stockData);

                // Send email notification
                _logger.LogDebug("Sending daily prediction report");
                await _notificationService.SendDailyPredictionReport(predictions);

                var jobDuration = DateTime.UtcNow - jobStartTime;
                _logger.LogInformation("Daily stock prediction job completed successfully in {Duration}", jobDuration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in daily stock prediction job after {Duration}", DateTime.UtcNow - jobStartTime);

                // Optionally re-throw if you want Quartz to handle the error
                // throw new JobExecutionException(ex, false);
            }
        }
    }

    public class StockPredictionJobScheduler
    {
        private const string JOB_IDENTITY = "stockPredictionJob";
        private const string JOB_GROUP = "group1";
        private const string TRIGGER_IDENTITY = "stockPredictionTrigger";
        private const string DEFAULT_SEND_TIME = "09:00";
        private const string TIME_PARSE_ERROR = "Invalid time format";

        public static async Task ScheduleJobs(IServiceProvider serviceProvider)
        {
            if (serviceProvider == null)
                throw new ArgumentNullException(nameof(serviceProvider));

            try
            {
                var schedulerFactory = serviceProvider.GetRequiredService<ISchedulerFactory>();
                var scheduler = await schedulerFactory.GetScheduler();

                if (scheduler == null)
                    throw new InvalidOperationException("Failed to get scheduler instance");

                // Check if job already exists
                var jobKey = new JobKey(JOB_IDENTITY, JOB_GROUP);
                if (await scheduler.CheckExists(jobKey))
                {
                    // Delete existing job to avoid duplicates
                    await scheduler.DeleteJob(jobKey);
                }

                var job = JobBuilder.Create<StockPredictionBackgroundService>()
                    .WithIdentity(jobKey)
                    .WithDescription("Daily stock prediction job")
                    .Build();

                var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                var notificationSettings = configuration.GetSection("NotificationSettings").Get<NotificationSettings>();

                // Parse send time with validation
                var sendTimeString = notificationSettings?.SendTime ?? DEFAULT_SEND_TIME;
                if (!TimeSpan.TryParse(sendTimeString, out var sendTime))
                {
                    sendTime = TimeSpan.Parse(DEFAULT_SEND_TIME);
                }

                // Validate time components
                var hours = Math.Clamp(sendTime.Hours, 0, 23);
                var minutes = Math.Clamp(sendTime.Minutes, 0, 59);

                // Schedule job to run daily at specified time
                var trigger = TriggerBuilder.Create()
                    .WithIdentity(TRIGGER_IDENTITY, JOB_GROUP)
                    .StartNow()
                    .WithSchedule(CronScheduleBuilder.DailyAtHourAndMinute(hours, minutes))
                    .WithDescription($"Daily trigger at {hours:D2}:{minutes:D2}")
                    .Build();

                await scheduler.ScheduleJob(job, trigger);

                // Verify job was scheduled
                var scheduledJob = await scheduler.GetJobDetail(jobKey);
                if (scheduledJob != null)
                {
                    var logger = serviceProvider.GetRequiredService<ILogger<StockPredictionJobScheduler>>();
                    logger.LogInformation("Stock prediction job scheduled successfully for {Hours:D2}:{Minutes:D2} daily", hours, minutes);
                }
            }
            catch (Exception ex)
            {
                var logger = serviceProvider.GetRequiredService<ILogger<StockPredictionJobScheduler>>();
                logger.LogError(ex, "Failed to schedule stock prediction job");
                throw; // Re-throw to prevent application from starting with invalid schedule
            }
        }

        // Optional: Method to reschedule job with different time
        public static async Task RescheduleJob(IServiceProvider serviceProvider, string newSendTime)
        {
            if (serviceProvider == null)
                throw new ArgumentNullException(nameof(serviceProvider));

            if (string.IsNullOrWhiteSpace(newSendTime))
                throw new ArgumentException("Send time cannot be empty", nameof(newSendTime));

            try
            {
                var schedulerFactory = serviceProvider.GetRequiredService<ISchedulerFactory>();
                var scheduler = await schedulerFactory.GetScheduler();

                var triggerKey = new TriggerKey(TRIGGER_IDENTITY, JOB_GROUP);

                if (!TimeSpan.TryParse(newSendTime, out var sendTime))
                {
                    throw new ArgumentException($"Invalid time format: {newSendTime}. Expected format: HH:mm");
                }

                var hours = Math.Clamp(sendTime.Hours, 0, 23);
                var minutes = Math.Clamp(sendTime.Minutes, 0, 59);

                var newTrigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .WithSchedule(CronScheduleBuilder.DailyAtHourAndMinute(hours, minutes))
                    .WithDescription($"Rescheduled daily trigger at {hours:D2}:{minutes:D2}")
                    .Build();

                await scheduler.RescheduleJob(triggerKey, newTrigger);

                var logger = serviceProvider.GetRequiredService<ILogger<StockPredictionJobScheduler>>();
                logger.LogInformation("Stock prediction job rescheduled to {Hours:D2}:{Minutes:D2} daily", hours, minutes);
            }
            catch (Exception ex)
            {
                var logger = serviceProvider.GetRequiredService<ILogger<StockPredictionJobScheduler>>();
                logger.LogError(ex, "Failed to reschedule stock prediction job");
                throw;
            }
        }
    }
}