using Microsoft.Extensions.Caching.Memory;
using StockNotificationApi.Interfaces;
using System.Collections.Concurrent;

namespace StockNotificationApi.Services
{
    public class MemoryCacheService : ICacheService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<MemoryCacheService> _logger;

        // Track cache keys for statistics and management
        private static readonly ConcurrentDictionary<string, DateTime> _cacheKeys = new();
        private static readonly object _statsLock = new();
        private static CacheStats _stats = new();

        public MemoryCacheService(IMemoryCache cache, ILogger<MemoryCacheService> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry)
        {
            return await GetOrSetAsync(key, factory, expiry, false);
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            return Get<T>(key);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Set(key, value, expiry);
        }

        public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry, bool slidingExpiration = false)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Fast path - check cache first
                if (_cache.TryGetValue(key, out T? cached))
                {
                    RecordHit(key, DateTime.UtcNow - startTime);
                    _logger.LogDebug("Cache hit for {Key}", key);
                    return cached;
                }

                _logger.LogDebug("Cache miss for {Key}, fetching...", key);

                // Add a timeout to prevent hanging - increased to 2 minutes for slow APIs
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                T? value = default;

                try
                {
                    value = await factory().WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Factory operation cancelled for key {Key}", key);
                    return default;
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timeout fetching data for key {Key} after 120 seconds", key);
                    // Set a short-lived negative cache to prevent immediate retry
                    _cache.Set(key, default(T), TimeSpan.FromSeconds(10));
                    return default;
                }

                if (value != null)
                {
                    var options = new MemoryCacheEntryOptions();

                    if (slidingExpiration)
                    {
                        options.SetSlidingExpiration(expiry);
                    }
                    else
                    {
                        options.SetAbsoluteExpiration(expiry);
                    }

                    options.RegisterPostEvictionCallback(OnCacheEntryEvicted);

                    _cache.Set(key, value, options);
                    _cacheKeys.TryAdd(key, DateTime.UtcNow);

                    RecordMiss(key, DateTime.UtcNow - startTime);
                    _logger.LogInformation("Cache set for {Key} with {ExpiryType} expiration of {Expiry}",
                        key, slidingExpiration ? "sliding" : "absolute", expiry);
                }
                else
                {
                    _logger.LogDebug("Factory returned null for {Key}, not caching", key);
                }

                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in GetOrSetAsync for key {Key}", key);
                // Set a short-lived negative cache on error to prevent immediate retry storms
                try
                {
                    _cache.Set(key, default(T), TimeSpan.FromSeconds(10));
                }
                catch (Exception cacheEx)
                {
                    _logger.LogDebug(cacheEx, "Failed to set negative cache for {Key}", key);
                }
                throw;
            }
        }

        public T? Get<T>(string key)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var value = _cache.TryGetValue(key, out T? cached) ? cached : default;

                if (value != null)
                {
                    RecordHit(key, DateTime.UtcNow - startTime);
                }

                return value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cache key {Key}", key);
                return default;
            }
        }

        public void Set<T>(string key, T value, TimeSpan expiry)
        {
            Set(key, value, expiry, false);
        }

        public void Set<T>(string key, T value, TimeSpan expiry, bool slidingExpiration)
        {
            try
            {
                var options = new MemoryCacheEntryOptions();

                if (slidingExpiration)
                {
                    options.SetSlidingExpiration(expiry);
                }
                else
                {
                    options.SetAbsoluteExpiration(expiry);
                }

                options.RegisterPostEvictionCallback(OnCacheEntryEvicted);

                _cache.Set(key, value, options);
                _cacheKeys.TryAdd(key, DateTime.UtcNow);

                _logger.LogDebug("Cache set for {Key} with {ExpiryType} expiration of {Expiry}",
                    key, slidingExpiration ? "sliding" : "absolute", expiry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting cache key {Key}", key);
                throw;
            }
        }

        public void Remove(string key)
        {
            try
            {
                _cache.Remove(key);
                _cacheKeys.TryRemove(key, out _);
                _logger.LogDebug("Cache entry removed for {Key}", key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing cache key {Key}", key);
            }
        }

        public bool TryGetValue<T>(string key, out T? value)
        {
            var success = _cache.TryGetValue(key, out T? cached);
            value = success ? cached : default;
            return success;
        }

        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new CacheStatistics
                {
                    TotalHits = _stats.Hits,
                    TotalMisses = _stats.Misses,
                    HitRate = _stats.Hits + _stats.Misses > 0
                        ? (double)_stats.Hits / (_stats.Hits + _stats.Misses) * 100
                        : 0,
                    AverageHitTimeMs = _stats.HitCount > 0
                        ? _stats.TotalHitTimeMs / _stats.HitCount
                        : 0,
                    AverageMissTimeMs = _stats.MissCount > 0
                        ? _stats.TotalMissTimeMs / _stats.MissCount
                        : 0,
                    CachedItemCount = _cacheKeys.Count,
                    CacheKeys = _cacheKeys.Keys.ToList()
                };
            }
        }

        /// <summary>
        /// Clears all cache entries
        /// </summary>
        public void Clear()
        {
            try
            {
                var keys = _cacheKeys.Keys.ToList();
                foreach (var key in keys)
                {
                    _cache.Remove(key);
                }
                _cacheKeys.Clear();

                lock (_statsLock)
                {
                    _stats = new CacheStats();
                }

                _logger.LogInformation("Cache cleared, removed {Count} entries", keys.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing cache");
            }
        }

        /// <summary>
        /// Removes cache entries matching a pattern
        /// </summary>
        public void RemoveByPattern(string pattern)
        {
            try
            {
                var wildcard = pattern.Replace("*", "").ToLower();
                var keysToRemove = _cacheKeys.Keys
                    .Where(k => k.ToLower().Contains(wildcard))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                    _cacheKeys.TryRemove(key, out _);
                }

                _logger.LogInformation("Removed {Count} cache entries matching pattern '{Pattern}'",
                    keysToRemove.Count, pattern);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing cache entries by pattern {Pattern}", pattern);
            }
        }

        /// <summary>
        /// Refreshes a cache entry by removing and re-fetching
        /// </summary>
        public async Task<T?> RefreshAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry, bool slidingExpiration = false)
        {
            Remove(key);
            return await GetOrSetAsync(key, factory, expiry, slidingExpiration);
        }

        #region Private Methods

        private void OnCacheEntryEvicted(object key, object? value, EvictionReason reason, object? state)
        {
            var keyStr = key?.ToString() ?? string.Empty;
            _cacheKeys.TryRemove(keyStr, out _);
            _logger.LogDebug("Cache entry evicted for {Key}, reason: {Reason}", key, reason);
        }

        private void RecordHit(string key, TimeSpan duration)
        {
            lock (_statsLock)
            {
                _stats.Hits++;
                _stats.HitCount++;
                _stats.TotalHitTimeMs += duration.TotalMilliseconds;
            }

            _logger.LogTrace("Cache hit for {Key} took {DurationMs:F1}ms", key, duration.TotalMilliseconds);
        }

        private void RecordMiss(string key, TimeSpan duration)
        {
            lock (_statsLock)
            {
                _stats.Misses++;
                _stats.MissCount++;
                _stats.TotalMissTimeMs += duration.TotalMilliseconds;
            }

            _logger.LogTrace("Cache miss for {Key} took {DurationMs:F1}ms to fetch", key, duration.TotalMilliseconds);
        }

        #endregion

        #region Helper Classes

        private class CacheStats
        {
            public long Hits { get; set; }
            public long Misses { get; set; }
            public long HitCount { get; set; }
            public long MissCount { get; set; }
            public double TotalHitTimeMs { get; set; }
            public double TotalMissTimeMs { get; set; }
        }

        public class CacheStatistics
        {
            public long TotalHits { get; set; }
            public long TotalMisses { get; set; }
            public double HitRate { get; set; }
            public double AverageHitTimeMs { get; set; }
            public double AverageMissTimeMs { get; set; }
            public int CachedItemCount { get; set; }
            public List<string> CacheKeys { get; set; } = new();
        }

        #endregion
    }
}