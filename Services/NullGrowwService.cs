// Services/NullGrowwService.cs
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;

namespace StockNotificationApi.Services
{
    public class NullGrowwService : IGrowwService
    {
        private readonly ILogger<NullGrowwService> _logger;

        public NullGrowwService(ILogger<NullGrowwService> logger)
        {
            _logger = logger;
        }

        public Task<GrowwSession> AuthenticateAsync()
        {
            _logger.LogDebug("NullGrowwService: AuthenticateAsync called (no-op)");
            return Task.FromResult(new GrowwSession
            {
                AccessToken = "mock-token",
                Expiry = DateTime.UtcNow.AddHours(2)
            });
        }

        public Task<OrderResponse> PlaceOrderAsync(OrderRequest order)
        {
            _logger.LogInformation("NullGrowwService: PlaceOrderAsync called for {Symbol} - SIMULATED (no actual order placed)",
                order?.Symbol);
            return Task.FromResult(new OrderResponse
            {
                OrderId = $"MOCK-{DateTime.Now.Ticks}",
                Status = "MOCK_SUCCESS",
                Message = "Mock order - no actual order placed",
                OrderTime = DateTime.Now
            });
        }

        public Task<decimal> GetLivePriceAsync(string symbol)
        {
            _logger.LogDebug("NullGrowwService: GetLivePriceAsync called for {Symbol}", symbol);
            return Task.FromResult(0m);
        }

        public Task<List<Position>> GetPositionsAsync()
        {
            _logger.LogDebug("NullGrowwService: GetPositionsAsync called");
            return Task.FromResult(new List<Position>());
        }
    }
}