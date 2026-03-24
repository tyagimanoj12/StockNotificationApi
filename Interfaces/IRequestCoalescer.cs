namespace StockNotificationApi.Interfaces
{
    public interface IRequestCoalescer
    {
        Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory);
    }
}
