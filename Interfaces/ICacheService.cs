// Interfaces/ICacheService.cs
using StockNotificationApi.Services;

namespace StockNotificationApi.Interfaces
{
    public interface ICacheService
    {
        // Existing methods
        Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry);
        Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry, bool slidingExpiration);
        T? Get<T>(string key);
        Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
        void Set<T>(string key, T value, TimeSpan expiry);
        void Set<T>(string key, T value, TimeSpan expiry, bool slidingExpiration);
        Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default);
        void Remove(string key);
        bool TryGetValue<T>(string key, out T? value);
        MemoryCacheService.CacheStatistics GetStatistics();
        void Clear();
        void RemoveByPattern(string pattern);
        Task<T?> RefreshAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry, bool slidingExpiration = false);
    }
}