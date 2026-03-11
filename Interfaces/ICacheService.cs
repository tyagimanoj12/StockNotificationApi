// Interfaces/ICacheService.cs
namespace StockNotificationApi.Interfaces
{
    public interface ICacheService
    {
        Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry);
        T? Get<T>(string key);
        void Set<T>(string key, T value, TimeSpan expiry);
        void Remove(string key);
    }
}