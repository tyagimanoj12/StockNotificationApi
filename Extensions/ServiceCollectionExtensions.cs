using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Serilog;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using StockNotificationApi.Services;
using StockNotificationApi.Services.AngelOne;
using System.Net;

namespace StockNotificationApi.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
        {
            // MemoryCache
            services.AddMemoryCache();
            services.AddSingleton<ICacheService, MemoryCacheService>();

            // HTTP Clients
            services.AddHttpClient();
            services.AddHttpClient("StockClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });
            services.AddHttpClient("GeminiClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

            // NSE API Service
            services.AddSingleton<INseApiService, NseApiService>();

            // Configure HTTP client for NSE
            services.AddHttpClient("NseClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate |
                                         DecompressionMethods.Brotli,
                UseCookies = true,
                AllowAutoRedirect = true
            });

            // Google Finance Service
            services.AddSingleton<IGoogleFinanceService, GoogleFinanceService>();

            // Moneycontrol Service
            services.AddSingleton<IMoneycontrolService, MoneycontrolService>();

            // Configure HTTP client with better settings
            services.AddHttpClient("GoogleFinance", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate,
                UseCookies = true,
                AllowAutoRedirect = true
            });

            services.AddHttpClient("Moneycontrol", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate,
                UseCookies = true,
                AllowAutoRedirect = true
            });

            // Price alert service
            services.AddSingleton<IPriceAlertService, PriceAlertService>();
            services.AddHostedService(sp => (PriceAlertService)sp.GetRequiredService<IPriceAlertService>());

            // Telegram client wrapper
            var telegramToken = configuration["TelegramSettings:BotToken"] ?? string.Empty;
            if (string.IsNullOrEmpty(telegramToken))
            {
                Log.Warning("Telegram bot token is not configured in TelegramSettings:BotToken");
            }

            services.AddSingleton<ITelegramClientWrapper>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<TelegramClientWrapper>>();
                return new TelegramClientWrapper(logger, telegramToken);
            });

            // Command processor - Hosted Service
            services.AddSingleton<ICommandProcessor, CommandProcessor>();
            services.AddHostedService(sp => (CommandProcessor)sp.GetRequiredService<ICommandProcessor>());

            services.AddSingleton<IRequestCoalescer, RequestCoalescer>();

            // FIX: Register TelegramBotService as Hosted Service only
            // Since it inherits from BackgroundService, it will be automatically discovered
            // Remove the manual IHostedService registration
            services.AddSingleton<ITelegramBotService, TelegramBotService>();
            // Add as hosted service using the same instance
            services.AddHostedService(sp => (TelegramBotService)sp.GetRequiredService<ITelegramBotService>());

            // Stock services
            services.AddScoped<IStockListService, StockListService>();
            services.AddScoped<IStockService, StockService>();

            try
            {
                services.Decorate<IStockService, FallbackStockService>();
                Log.Information("FallbackStockService registered via Scrutor");
            }
            catch (InvalidOperationException)
            {
                Log.Warning("Scrutor not installed. Install with: dotnet add package Scrutor");
            }

            services.AddHttpClient<IAIService, GeminiService>();

            services.AddScoped<INotificationService, EmailNotificationService>();
            services.AddScoped<INewsService, NewsService>();
            services.AddScoped<IDailyBriefingService, DailyBriefingService>();
            services.AddScoped<IPortfolioService, PortfolioService>();
            services.AddScoped<IEnhancedMarketAnalysisService, EnhancedMarketAnalysisService>();
            services.AddScoped<ITradingService, TradingService>();
            services.AddSingleton<IMarketStatusService, MarketStatusService>();
            services.AddSingleton<ICircuitBreakerService, CircuitBreakerService>();

            services.AddHttpClient("AngelOne");
            services.AddScoped<IAngelOneService, AngelOneService>();

            services.AddScoped<HoldingOptimizer>();

            var growwApiKey = configuration["Groww:ApiKey"];
            var growwApiSecret = configuration["Groww:ApiSecret"];
            var useGroww = !string.IsNullOrEmpty(growwApiKey) && !string.IsNullOrEmpty(growwApiSecret);

            if (useGroww)
            {
                services.AddScoped<IGrowwService, GrowwService>();
                Log.Information("Groww service registered");
            }
            else
            {
                services.AddScoped<IGrowwService, NullGrowwService>();
                Log.Information("Groww service not configured");
            }

            // AutoTradingService - Hosted Service
            services.AddHostedService<AutoTradingService>();

            // Quartz
            services.AddQuartz(q =>
            {
                q.UseMicrosoftDependencyInjectionJobFactory();

                var jobKey = new JobKey("StockPredictionJob");
                q.AddJob<StockPredictionBackgroundService>(opts => opts.WithIdentity(jobKey));

                q.AddTrigger(opts => opts
                    .ForJob(jobKey)
                    .WithIdentity("StockPredictionJob-trigger")
                    .WithCronSchedule("0 0 9 * * ?"));
            });

            services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

            // Settings
            services.Configure<NotificationSettings>(configuration.GetSection("NotificationSettings"));
            services.Configure<GeminiAISettings>(configuration.GetSection("GeminiAISettings"));
            services.Configure<StockApiSettings>(configuration.GetSection("StockApiSettings"));
            services.Configure<EmailSettings>(configuration.GetSection("EmailSettings"));

            // Analyzers and formatters
            services.AddScoped<IPortfolioAnalyzer, PortfolioAnalyzer>();
            services.AddScoped<IMessageFormatter, MessageFormatter>();

            return services;
        }
    }
}