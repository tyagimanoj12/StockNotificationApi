using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StockNotificationApi.Interfaces;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace StockNotificationApi.Services
{
    public class CommandProcessor : BackgroundService, ICommandProcessor
    {
        private readonly Channel<(long ChatId, Func<Task> Work)> _channel;
        private readonly ILogger<CommandProcessor> _logger;

        // Track active processing tasks for graceful shutdown
        private readonly HashSet<Task> _activeProcessingTasks = new();
        private readonly SemaphoreSlim _tasksLock = new(1, 1);

        // Configuration
        private const int COMMAND_DELAY_MS = 200; // Delay between commands per user
        private const int MAX_CONCURRENT_PER_USER = 1; // Only one command per user at a time
        private const int CHANNEL_CAPACITY = 1000; // Prevent unbounded growth
        private const int MAX_PROCESSING_TIME_SECONDS = 30; // Maximum time per command

        public CommandProcessor(ILogger<CommandProcessor> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Use bounded channel to prevent memory issues during high load
            _channel = Channel.CreateBounded<(long, Func<Task>)>(new BoundedChannelOptions(CHANNEL_CAPACITY)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public async Task EnqueueAsync(long chatId, Func<Task> work)
        {
            try
            {
                _logger.LogDebug("Enqueuing work for chat {ChatId}", chatId);

                // Check if we can write to the channel
                if (!_channel.Writer.TryWrite((chatId, work)))
                {
                    // If channel is full, wait asynchronously
                    await _channel.Writer.WriteAsync((chatId, work));
                }
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning("Channel closed, cannot enqueue work for chat {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueuing work for chat {ChatId}", chatId);
                throw;
            }
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("CommandProcessor stopping via interface method...");
            await StopAsync(CancellationToken.None);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("CommandProcessor stopping...");

            try
            {
                // Mark channel as complete - no more items will be accepted
                _channel.Writer.TryComplete();

                // Wait for all active processing tasks to complete (with timeout)
                await _tasksLock.WaitAsync(cancellationToken);
                try
                {
                    if (_activeProcessingTasks.Any())
                    {
                        _logger.LogInformation("Waiting for {Count} active processing tasks to complete",
                            _activeProcessingTasks.Count);

                        var timeout = TimeSpan.FromSeconds(10);
                        var allTasks = Task.WhenAll(_activeProcessingTasks);
                        var completedTask = await Task.WhenAny(allTasks, Task.Delay(timeout, cancellationToken));

                        if (completedTask == allTasks)
                        {
                            _logger.LogInformation("All processing tasks completed");
                        }
                        else
                        {
                            _logger.LogWarning("Timeout waiting for processing tasks");
                        }
                    }
                }
                finally
                {
                    _tasksLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stop operation cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during CommandProcessor stop");
            }

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("CommandProcessor stopped");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("CommandProcessor started");

            // Use concurrent dictionary for per-chat queues
            var perChatQueues = new ConcurrentDictionary<long, Queue<Func<Task>>>();
            var processingStatus = new ConcurrentDictionary<long, bool>();

            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        // Get or create queue for this chat
                        var queue = perChatQueues.GetOrAdd(item.ChatId, _ => new Queue<Func<Task>>());

                        lock (queue)
                        {
                            queue.Enqueue(item.Work);
                        }

                        // Start processing for this chat if not already processing
                        var isProcessing = processingStatus.GetOrAdd(item.ChatId, false);

                        if (!isProcessing)
                        {
                            processingStatus[item.ChatId] = true;

                            // Start processing task and track it
                            var processingTask = ProcessQueueForChatAsync(
                                item.ChatId,
                                perChatQueues,
                                processingStatus,
                                stoppingToken);

                            await TrackProcessingTaskAsync(processingTask, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Channel read cancelled");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing channel item");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("CommandProcessor execution cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CommandProcessor execution terminated unexpectedly");
            }

            _logger.LogInformation("CommandProcessor execution completed");
        }

        private async Task ProcessQueueForChatAsync(
            long chatId,
            ConcurrentDictionary<long, Queue<Func<Task>>> perChatQueues,
            ConcurrentDictionary<long, bool> processingStatus,
            CancellationToken stoppingToken)
        {
            _logger.LogDebug("Started processing queue for chat {ChatId}", chatId);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    Func<Task>? command = null;

                    if (perChatQueues.TryGetValue(chatId, out var queue))
                    {
                        lock (queue)
                        {
                            if (queue.Count > 0)
                            {
                                command = queue.Dequeue();
                            }
                        }
                    }

                    if (command == null)
                    {
                        // No more commands, exit
                        break;
                    }

                    try
                    {
                        // Execute the command with timeout protection
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(MAX_PROCESSING_TIME_SECONDS));

                        await command().WaitAsync(cts.Token);
                        _logger.LogDebug("Command processed for chat {ChatId}", chatId);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Command timeout or cancelled for chat {ChatId}", chatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing command for chat {ChatId}", chatId);
                    }

                    // Rate limit between commands for this user
                    try
                    {
                        await Task.Delay(COMMAND_DELAY_MS, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // Mark processing as complete for this chat
                processingStatus.TryRemove(chatId, out _);
                _logger.LogDebug("Stopped processing queue for chat {ChatId}", chatId);

                // Clean up empty queues to prevent memory growth
                if (perChatQueues.TryGetValue(chatId, out var queue))
                {
                    lock (queue)
                    {
                        if (queue.Count == 0)
                        {
                            perChatQueues.TryRemove(chatId, out _);
                        }
                    }
                }
            }
        }

        private async Task TrackProcessingTaskAsync(Task task, CancellationToken stoppingToken)
        {
            await _tasksLock.WaitAsync(stoppingToken);
            try
            {
                _activeProcessingTasks.Add(task);
            }
            finally
            {
                _tasksLock.Release();
            }

            // Remove task when completed
            _ = task.ContinueWith(async t =>
            {
                await _tasksLock.WaitAsync();
                try
                {
                    _activeProcessingTasks.Remove(t);
                }
                finally
                {
                    _tasksLock.Release();
                }

                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.LogError(t.Exception, "Processing task faulted");
                }
            }, TaskScheduler.Default);
        }

        public (int QueuedItems, int ActiveProcesses) GetStatistics()
        {
            return (_channel.Reader.Count, _activeProcessingTasks.Count);
        }

        public bool ClearUserQueue(long chatId)
        {
            // Note: This is a simplified implementation
            // A full implementation would need to filter the channel
            _logger.LogInformation("ClearUserQueue called for {ChatId} - would need channel support", chatId);
            return false;
        }
    }
}