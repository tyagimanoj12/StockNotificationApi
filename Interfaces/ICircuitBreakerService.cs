namespace StockNotificationApi.Interfaces
{
    // Services/CircuitBreakerService.cs
    public interface ICircuitBreakerService
    {
        Task<T> ExecuteAsync<T>(string apiName, Func<Task<T>> action, T fallbackValue = default);
        Task<bool> IsApiAvailableAsync(string apiName); // Make this return Task<bool>
    }
}
