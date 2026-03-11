// Services/MemoryCacheService.cs
using Microsoft.Extensions.Caching.Memory;
using StockNotificationApi.Interfaces;

namespace StockNotificationApi.Services
{
    public class MemoryCacheService : ICacheService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<MemoryCacheService> _logger;

        public MemoryCacheService(IMemoryCache cache, ILogger<MemoryCacheService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry)
        {
            if (_cache.TryGetValue(key, out T? cached))
            {
                _logger.LogDebug("Cache hit for {Key}", key);
                return cached;
            }

            _logger.LogDebug("Cache miss for {Key}, fetching...", key);
            var value = await factory();

            if (value != null)
            {
                _cache.Set(key, value, expiry);
            }

            return value;
        }

        public T? Get<T>(string key)
        {
            return _cache.TryGetValue(key, out T? value) ? value : default;
        }

        public void Set<T>(string key, T value, TimeSpan expiry)
        {
            _cache.Set(key, value, expiry);
        }

        public void Remove(string key)
        {
            _cache.Remove(key);
        }
    }
}