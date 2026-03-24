using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Services
{
    public class RequestCoalescer : IRequestCoalescer
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, Task> _pendingRequests = new();
        private readonly ILogger<RequestCoalescer> _logger;

        public RequestCoalescer(ILogger<RequestCoalescer> logger)
        {
            _logger = logger;
        }

        public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory)
        {
            Task<T>? task = null;

            try
            {
                await _semaphore.WaitAsync();

                // Check if there's already a pending request for this key
                if (_pendingRequests.TryGetValue(key, out var existingTask))
                {
                    _logger.LogDebug("Coalescing duplicate request for {Key}", key);
                    return await (Task<T>)existingTask;
                }

                // Create new task and store it
                task = factory();
                _pendingRequests[key] = task;

                _semaphore.Release();
                _logger.LogDebug("Created new request for {Key}", key);

                // Wait for the task to complete outside the lock
                var result = await task;

                return result;
            }
            finally
            {
                if (task != null)
                {
                    await _semaphore.WaitAsync();
                    _pendingRequests.Remove(key);
                    _semaphore.Release();
                }
            }
        }
    }
}
