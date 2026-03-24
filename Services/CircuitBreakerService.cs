using StockNotificationApi.Interfaces;
using System.Collections.Concurrent;

namespace StockNotificationApi.Services
{
    public class CircuitBreakerService : ICircuitBreakerService
    {
        private readonly ILogger<CircuitBreakerService> _logger;
        private readonly ConcurrentDictionary<string, CircuitBreaker> _circuitBreakers = new();
        private readonly ConcurrentDictionary<string, TimeoutInfo> _timeoutTracking = new();
        private readonly ConcurrentDictionary<string, DateTime> _rateLimitTracking = new();
        private readonly Timer _cleanupTimer;

        // Increased thresholds for better tolerance
        private const int DEFAULT_FAILURE_THRESHOLD = 10;      // Increased from 3
        private const int DEFAULT_OPEN_TIMEOUT_MINUTES = 5;    // Increased from 2
        private const int DEFAULT_HALF_OPEN_SECONDS = 60;      // Increased from 30

        public CircuitBreakerService(ILogger<CircuitBreakerService> logger)
        {
            _logger = logger;
            _cleanupTimer = new Timer(CleanupOldEntries, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public async Task<T> ExecuteAsync<T>(string apiName, Func<Task<T>> action, T fallbackValue = default)
        {
            // Check for rate limiting first
            if (IsRateLimited(apiName))
            {
                _logger.LogWarning("API {ApiName} is rate limited. Using fallback.", apiName);
                return fallbackValue;
            }

            // Check for recent timeouts
            if (HasTimedOutRecently(apiName))
            {
                _logger.LogWarning("API {ApiName} recently timed out. Using fallback.", apiName);
                return fallbackValue;
            }

            var breaker = GetOrCreateBreaker(apiName);

            if (!breaker.IsClosed)
            {
                _logger.LogWarning("Circuit breaker for {ApiName} is open. Using fallback.", apiName);
                return fallbackValue;
            }

            try
            {
                var result = await action();
                breaker.RecordSuccess();
                ClearRateLimit(apiName);
                return result;
            }
            catch (Exception ex)
            {
                // Don't count "unknown symbol token" as a failure - these are expected
                if (ex.Message.Contains("Unknown symbol token") || ex.Message.Contains("Please add to SymbolTokenMap"))
                {
                    _logger.LogDebug("Skipping circuit breaker failure for {ApiName} due to missing symbol token", apiName);
                    return fallbackValue;
                }

                breaker.RecordFailure();

                if (IsTimeoutException(ex))
                {
                    RecordTimeout(apiName);
                }

                if (IsRateLimitException(ex))
                {
                    RecordRateLimit(apiName);
                }

                _logger.LogWarning(ex, "API {ApiName} failed. Circuit breaker state: {State}, Failures: {FailureCount}/{Threshold}",
                    apiName, breaker.State, breaker.FailureCount, breaker.FailureThreshold);

                if (!breaker.IsClosed)
                {
                    return fallbackValue;
                }
                throw;
            }
        }

        public async Task<bool> IsApiAvailableAsync(string apiName)
        {
            await Task.CompletedTask;
            return await IsApiAvailable(apiName);
        }

        public async Task<bool> IsApiAvailable(string apiName)
        {
            var breaker = GetOrCreateBreaker(apiName);
            var hasTimedOut = await HasTimedOutRecentlyAsync(apiName);
            var isRateLimited = await IsRateLimitedAsync(apiName);
            return breaker.IsClosed && !hasTimedOut && !isRateLimited;
        }

        private Task<bool> HasTimedOutRecentlyAsync(string apiName)
        {
            if (_timeoutTracking.TryGetValue(apiName, out var info))
            {
                if (DateTime.UtcNow < info.TimeoutUntil)
                    return Task.FromResult(true);
                _timeoutTracking.TryRemove(apiName, out _);
            }
            return Task.FromResult(false);
        }

        private Task<bool> IsRateLimitedAsync(string apiName)
        {
            if (_rateLimitTracking.TryGetValue(apiName, out var limitedUntil))
            {
                if (DateTime.UtcNow < limitedUntil)
                    return Task.FromResult(true);
                _rateLimitTracking.TryRemove(apiName, out _);
            }
            return Task.FromResult(false);
        }

        public bool IsRateLimited(string apiName)
        {
            if (_rateLimitTracking.TryGetValue(apiName, out var limitedUntil))
            {
                if (DateTime.UtcNow < limitedUntil)
                    return true;
                _rateLimitTracking.TryRemove(apiName, out _);
            }
            return false;
        }

        public void RecordRateLimit(string apiName, int cooldownMinutes = 5)
        {
            var cooldownUntil = DateTime.UtcNow.AddMinutes(cooldownMinutes);
            _rateLimitTracking.AddOrUpdate(apiName, cooldownUntil, (_, _) => cooldownUntil);
            _logger.LogWarning("API {ApiName} rate limited. Cooldown until {CooldownUntil}",
                apiName, cooldownUntil);
        }

        public void ClearRateLimit(string apiName)
        {
            _rateLimitTracking.TryRemove(apiName, out _);
        }

        public bool HasTimedOutRecently(string apiName)
        {
            if (_timeoutTracking.TryGetValue(apiName, out var info))
            {
                if (DateTime.UtcNow < info.TimeoutUntil)
                    return true;
                _timeoutTracking.TryRemove(apiName, out _);
            }
            return false;
        }

        public void RecordTimeout(string apiName)
        {
            _timeoutTracking.AddOrUpdate(apiName,
                new TimeoutInfo
                {
                    TimeoutUntil = DateTime.UtcNow.AddMinutes(5),
                    TimeoutCount = 1
                },
                (_, info) =>
                {
                    info.TimeoutCount++;
                    var backoffMinutes = Math.Min(30, 5 * info.TimeoutCount);
                    info.TimeoutUntil = DateTime.UtcNow.AddMinutes(backoffMinutes);
                    _logger.LogWarning("API {ApiName} timed out again (count: {Count}). Cooldown extended to {Minutes} minutes",
                        apiName, info.TimeoutCount, backoffMinutes);
                    return info;
                });
        }

        private bool IsTimeoutException(Exception ex)
        {
            return ex is TimeoutException ||
                   ex is TaskCanceledException ||
                   ex is OperationCanceledException ||
                   (ex.InnerException != null && IsTimeoutException(ex.InnerException)) ||
                   ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsRateLimitException(Exception ex)
        {
            return ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase);
        }

        private void CleanupOldEntries(object state)
        {
            try
            {
                var now = DateTime.UtcNow;

                // Clean up timeout tracking
                var expiredTimeouts = _timeoutTracking
                    .Where(kvp => kvp.Value.TimeoutUntil < now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredTimeouts)
                {
                    _timeoutTracking.TryRemove(key, out _);
                }

                // Clean up rate limit tracking
                var expiredRateLimits = _rateLimitTracking
                    .Where(kvp => kvp.Value < now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredRateLimits)
                {
                    _rateLimitTracking.TryRemove(key, out _);
                }

                // Check circuit breaker timeouts
                foreach (var breaker in _circuitBreakers.Values)
                {
                    breaker.CheckTimeout();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup of circuit breaker entries");
            }
        }

        private CircuitBreaker GetOrCreateBreaker(string apiName)
        {
            return _circuitBreakers.GetOrAdd(apiName, key => new CircuitBreaker(key, _logger,
                DEFAULT_FAILURE_THRESHOLD, DEFAULT_OPEN_TIMEOUT_MINUTES, DEFAULT_HALF_OPEN_SECONDS));
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }

        private class CircuitBreaker
        {
            private readonly string _apiName;
            private readonly ILogger _logger;
            private readonly int _failureThreshold;
            private readonly TimeSpan _openTimeout;
            private readonly TimeSpan _halfOpenTimeout;
            private readonly object _lock = new();

            private int _failureCount;
            private DateTime _lastFailureTime;
            private CircuitState _state = CircuitState.Closed;

            public enum CircuitState { Closed, Open, HalfOpen }
            public CircuitState State => _state;
            public bool IsClosed => _state == CircuitState.Closed;
            public int FailureCount => _failureCount;
            public int FailureThreshold => _failureThreshold;

            public CircuitBreaker(string apiName, ILogger logger, int failureThreshold, int openTimeoutMinutes, int halfOpenSeconds)
            {
                _apiName = apiName;
                _logger = logger;
                _failureThreshold = failureThreshold;
                _openTimeout = TimeSpan.FromMinutes(openTimeoutMinutes);
                _halfOpenTimeout = TimeSpan.FromSeconds(halfOpenSeconds);
            }

            public void RecordSuccess()
            {
                lock (_lock)
                {
                    if (_state == CircuitState.HalfOpen)
                    {
                        Reset();
                        _logger.LogInformation("Circuit breaker for {ApiName} closed after successful test", _apiName);
                    }
                    else
                    {
                        // Reduce failure count on success (allow recovery)
                        if (_failureCount > 0)
                        {
                            _failureCount = Math.Max(0, _failureCount - 1);
                            _logger.LogDebug("Circuit breaker for {ApiName} reduced failure count to {Count}",
                                _apiName, _failureCount);
                        }
                    }
                }
            }

            public void RecordFailure()
            {
                lock (_lock)
                {
                    _failureCount++;
                    _lastFailureTime = DateTime.UtcNow;

                    if (_failureCount >= _failureThreshold && _state == CircuitState.Closed)
                    {
                        _state = CircuitState.Open;
                        _logger.LogWarning("Circuit breaker for {ApiName} opened after {Count} failures (threshold: {Threshold})",
                            _apiName, _failureCount, _failureThreshold);
                    }
                    else if (_state == CircuitState.HalfOpen)
                    {
                        _state = CircuitState.Open;
                        _logger.LogWarning("Circuit breaker for {ApiName} reopened after half-open failure", _apiName);
                    }
                    else
                    {
                        _logger.LogDebug("Circuit breaker for {ApiName} recorded failure {Count}/{Threshold}",
                            _apiName, _failureCount, _failureThreshold);
                    }
                }
            }

            public void CheckTimeout()
            {
                lock (_lock)
                {
                    if (_state == CircuitState.Open && DateTime.UtcNow - _lastFailureTime > _openTimeout)
                    {
                        _state = CircuitState.HalfOpen;
                        _logger.LogInformation("Circuit breaker for {ApiName} moved to half-open after {TimeoutMinutes} minutes",
                            _apiName, _openTimeout.TotalMinutes);
                    }
                }
            }

            private void Reset()
            {
                _failureCount = 0;
                _state = CircuitState.Closed;
            }
        }
    }

    public class TimeoutInfo
    {
        public DateTime TimeoutUntil { get; set; }
        public int TimeoutCount { get; set; }
    }
}