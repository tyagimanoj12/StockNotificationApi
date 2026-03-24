using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text.Json;
using System.Collections.Concurrent;

namespace StockNotificationApi.Services
{
    public class PortfolioService : IPortfolioService
    {
        private readonly ILogger<PortfolioService> _logger;
        private readonly IStockService _stockService;
        private readonly IServiceProvider _serviceProvider;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly string _dataPath;

        // In-memory cache for portfolios
        private static readonly ConcurrentDictionary<long, CachedPortfolio> _portfolioCache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

        // Track last cache cleanup
        private static DateTime _lastCacheCleanup = DateTime.UtcNow;
        private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(5);

        // Use constants from Constants.cs
        private const string DATA_DIRECTORY = "portfolio_data";
        private const string FILE_EXTENSION = ".json";
        private const decimal PROFIT_THRESHOLD = PortfolioConstants.PROFIT_THRESHOLD;
        private const int MAX_HOLDINGS_PER_USER = PortfolioConstants.MAX_HOLDINGS_PER_USER;
        private const int MIN_QUANTITY = PortfolioConstants.MIN_QUANTITY;
        private const decimal MIN_PRICE = PortfolioConstants.MIN_PRICE;
        private const int MAX_CACHE_ENTRIES = 1000;

        public PortfolioService(
            ILogger<PortfolioService> logger,
            IStockService stockService,
            IWebHostEnvironment env,
            IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            if (env == null) throw new ArgumentNullException(nameof(env));

            _dataPath = Path.Combine(env.ContentRootPath, DATA_DIRECTORY);
            EnsureDirectoryExists();

            // Start background cache cleanup
            _ = Task.Run(() => CleanupCachePeriodically());
        }

        private void EnsureDirectoryExists()
        {
            try
            {
                if (!Directory.Exists(_dataPath))
                {
                    Directory.CreateDirectory(_dataPath);
                    _logger.LogInformation("Created portfolio data directory at {Path}", _dataPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create portfolio data directory at {Path}", _dataPath);
                throw;
            }
        }

        private async Task CleanupCachePeriodically()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(CacheCleanupInterval);

                    var now = DateTime.UtcNow;
                    var expiredKeys = _portfolioCache
                        .Where(kvp => kvp.Value.Expiry < now)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _portfolioCache.TryRemove(key, out _);
                    }

                    // Trim cache if too large
                    if (_portfolioCache.Count > MAX_CACHE_ENTRIES)
                    {
                        var toRemove = _portfolioCache
                            .OrderBy(kvp => kvp.Value.Expiry)
                            .Take(_portfolioCache.Count - MAX_CACHE_ENTRIES)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in toRemove)
                        {
                            _portfolioCache.TryRemove(key, out _);
                        }

                        _logger.LogDebug("Trimmed portfolio cache, removed {Count} entries", toRemove.Count);
                    }

                    _lastCacheCleanup = now;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in cache cleanup");
                }
            }
        }

        public async Task<UserPortfolio?> GetUserPortfolioAsync(long chatId)
        {
            if (chatId <= 0)
            {
                _logger.LogWarning("Invalid chat ID: {ChatId}", chatId);
                return null;
            }

            // Check cache first
            if (_portfolioCache.TryGetValue(chatId, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                _logger.LogDebug("Cache hit for portfolio {ChatId}", chatId);
                return cached.Portfolio;
            }

            // Load from disk
            var portfolio = await LoadPortfolioFromDisk(chatId);

            if (portfolio != null)
            {
                // Cache with expiration
                _portfolioCache[chatId] = new CachedPortfolio
                {
                    Portfolio = portfolio,
                    Expiry = DateTime.UtcNow.Add(CacheDuration)
                };
                _logger.LogDebug("Cached portfolio for {ChatId}", chatId);
            }

            return portfolio;
        }

        private async Task<UserPortfolio?> LoadPortfolioFromDisk(long chatId)
        {
            var filePath = GetFilePath(chatId);

            if (!File.Exists(filePath))
            {
                _logger.LogDebug("No portfolio found for chat {ChatId}", chatId);
                return null;
            }

            try
            {
                await _fileLock.WaitAsync();
                try
                {
                    var json = await File.ReadAllTextAsync(filePath);
                    var portfolio = JsonSerializer.Deserialize<UserPortfolio>(json);

                    if (portfolio == null)
                    {
                        _logger.LogWarning("Deserialized null portfolio for chat {ChatId}", chatId);
                        return null;
                    }

                    _logger.LogDebug("Successfully loaded portfolio for chat {ChatId} with {Count} holdings",
                        chatId, portfolio.Holdings?.Count ?? 0);

                    return portfolio;
                }
                finally
                {
                    _fileLock.Release();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON error loading portfolio for chat {ChatId}", chatId);
                return null;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO error loading portfolio for chat {ChatId}", chatId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error loading portfolio for chat {ChatId}", chatId);
                return null;
            }
        }

        public async Task<bool> AddHoldingAsync(long chatId, string username, PortfolioHolding holding)
        {
            if (chatId <= 0)
            {
                _logger.LogWarning("Invalid chat ID for adding holding");
                return false;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                _logger.LogWarning("Empty username for chat {ChatId}", chatId);
                return false;
            }

            if (!IsValidHolding(holding))
            {
                _logger.LogWarning("Invalid holding data for {Symbol}", holding?.Symbol);
                return false;
            }

            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId) ?? CreateNewPortfolio(chatId, username);

                // Check if user has too many holdings
                if (portfolio.Holdings.Count >= MAX_HOLDINGS_PER_USER)
                {
                    _logger.LogWarning("User {ChatId} has reached maximum holdings limit", chatId);
                    return false;
                }

                var existing = portfolio.Holdings.FirstOrDefault(h =>
                    h.Symbol.Equals(holding.Symbol, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    UpdateExistingHolding(existing, holding);
                }
                else
                {
                    portfolio.Holdings.Add(holding);
                }

                portfolio.LastUpdated = DateTime.UtcNow;
                await SavePortfolioAsync(portfolio);

                // Invalidate cache
                _portfolioCache.TryRemove(chatId, out _);

                _logger.LogInformation("Added/updated holding {Symbol} for user {ChatId}", holding.Symbol, chatId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding holding for chat {ChatId}", chatId);
                return false;
            }
        }

        private bool IsValidHolding(PortfolioHolding? holding)
        {
            if (holding == null) return false;
            if (string.IsNullOrWhiteSpace(holding.Symbol)) return false;
            if (holding.Quantity < MIN_QUANTITY) return false;
            if (holding.BuyPrice < MIN_PRICE) return false;
            return true;
        }

        private UserPortfolio CreateNewPortfolio(long chatId, string username)
        {
            return new UserPortfolio
            {
                ChatId = chatId,
                Username = username,
                Holdings = new List<PortfolioHolding>(),
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };
        }

        private void UpdateExistingHolding(PortfolioHolding existing, PortfolioHolding newHolding)
        {
            // Calculate new average price
            var totalQuantity = existing.Quantity + newHolding.Quantity;
            var totalValue = (existing.Quantity * existing.BuyPrice) + (newHolding.Quantity * newHolding.BuyPrice);

            existing.Quantity = totalQuantity;
            existing.BuyPrice = totalValue / totalQuantity;
            existing.Notes = CombineNotes(existing.Notes, newHolding.Notes);
        }

        private string? CombineNotes(string? existingNotes, string? newNotes)
        {
            if (string.IsNullOrWhiteSpace(newNotes)) return existingNotes;
            if (string.IsNullOrWhiteSpace(existingNotes)) return newNotes;
            return $"{existingNotes}; {newNotes}";
        }

        public async Task<bool> RemoveHoldingAsync(long chatId, string symbol)
        {
            if (chatId <= 0 || string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogWarning("Invalid parameters for removing holding");
                return false;
            }

            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId);
                if (portfolio == null)
                {
                    _logger.LogWarning("Portfolio not found for chat {ChatId}", chatId);
                    return false;
                }

                var holding = portfolio.Holdings.FirstOrDefault(h =>
                    h.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                if (holding == null)
                {
                    _logger.LogWarning("Holding {Symbol} not found for chat {ChatId}", symbol, chatId);
                    return false;
                }

                portfolio.Holdings.Remove(holding);
                portfolio.LastUpdated = DateTime.UtcNow;

                await SavePortfolioAsync(portfolio);

                // Invalidate cache
                _portfolioCache.TryRemove(chatId, out _);

                _logger.LogInformation("Removed holding {Symbol} for user {ChatId}", symbol, chatId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing holding for chat {ChatId}", chatId);
                return false;
            }
        }

        public async Task<bool> UpdateHoldingAsync(long chatId, PortfolioHolding holding)
        {
            if (chatId <= 0 || !IsValidHolding(holding))
            {
                _logger.LogWarning("Invalid parameters for updating holding");
                return false;
            }

            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId);
                if (portfolio == null)
                {
                    _logger.LogWarning("Portfolio not found for chat {ChatId}", chatId);
                    return false;
                }

                var existing = portfolio.Holdings.FirstOrDefault(h =>
                    h.Symbol.Equals(holding.Symbol, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    _logger.LogWarning("Holding {Symbol} not found for chat {ChatId}", holding.Symbol, chatId);
                    return false;
                }

                existing.Quantity = holding.Quantity;
                existing.BuyPrice = holding.BuyPrice;
                existing.Notes = holding.Notes;
                portfolio.LastUpdated = DateTime.UtcNow;

                await SavePortfolioAsync(portfolio);

                // Invalidate cache
                _portfolioCache.TryRemove(chatId, out _);

                _logger.LogInformation("Updated holding {Symbol} for user {ChatId}", holding.Symbol, chatId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating holding for chat {ChatId}", chatId);
                return false;
            }
        }

        public async Task<PortfolioPerformance?> GetPortfolioPerformanceAsync(long chatId)
        {
            if (chatId <= 0)
            {
                _logger.LogWarning("Invalid chat ID for performance calculation");
                return null;
            }

            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId);
                if (portfolio?.Holdings == null || !portfolio.Holdings.Any())
                {
                    _logger.LogDebug("No holdings found for chat {ChatId}", chatId);
                    return null;
                }

                // ========== FIX: Use ONE bulk call instead of individual calls ==========

                // Collect all symbols from holdings
                var symbols = portfolio.Holdings
                    .Where(h => h != null && !string.IsNullOrEmpty(h.Symbol))
                    .Select(h => h.Symbol)
                    .ToList();

                if (!symbols.Any())
                {
                    _logger.LogDebug("No valid symbols found for chat {ChatId}", chatId);
                    return null;
                }

                _logger.LogInformation("Fetching current prices for {Count} holdings in a single bulk call", symbols.Count);

                // Make ONE bulk call to get all stock data
                var stockDataList = await _stockService.GetMultipleQuotesAsync(symbols.Select(s => $"{s}.NS").ToList());

                // Create a dictionary for quick lookup
                var stockDataDict = stockDataList
                    .Where(s => s != null)
                    .ToDictionary(s => s.Symbol, s => s, StringComparer.OrdinalIgnoreCase);

                _logger.LogInformation("✅ Bulk fetch returned {Count} stock data entries", stockDataDict.Count);

                var performance = new PortfolioPerformance
                {
                    Holdings = new List<HoldingPerformance>(),
                    SectorAllocation = new Dictionary<string, decimal>()
                };

                var sectorAllocation = new Dictionary<string, decimal>();

                // Process each holding using the dictionary lookup
                foreach (var holding in portfolio.Holdings.Where(h => h != null))
                {
                    // Try to get stock data from the dictionary
                    if (!stockDataDict.TryGetValue(holding.Symbol, out var stock))
                    {
                        _logger.LogWarning("Could not fetch current price for {Symbol}", holding.Symbol);
                        continue;
                    }

                    var holdingPerf = new HoldingPerformance
                    {
                        Symbol = holding.Symbol,
                        CompanyName = stock.Name,
                        Quantity = holding.Quantity,
                        BuyPrice = holding.BuyPrice,
                        CurrentPrice = stock.Price,
                        DayChange = stock.Change,
                        DayChangePercent = stock.ChangePercent,
                        Sector = stock.Sector ?? "Other"
                    };

                    performance.Holdings.Add(holdingPerf);
                    performance.TotalInvestment += holdingPerf.Investment;
                    performance.CurrentValue += holdingPerf.CurrentValue;
                    performance.TodayPL += holdingPerf.Quantity * holdingPerf.DayChange;

                    UpdateSectorAllocation(sectorAllocation, holdingPerf);
                }

                CalculatePortfolioTotals(performance, sectorAllocation);

                _logger.LogInformation("Calculated performance for chat {ChatId}: P&L: {PL:F2} ({PLPercent:F2}%)",
                    chatId, performance.TotalPL, performance.TotalPLPercent);

                return performance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating portfolio performance for chat {ChatId}", chatId);
                return null;
            }
        }

        private void UpdateSectorAllocation(Dictionary<string, decimal> sectorAllocation, HoldingPerformance holdingPerf)
        {
            var sector = holdingPerf.Sector ?? "Other";

            if (sectorAllocation.ContainsKey(sector))
            {
                sectorAllocation[sector] += holdingPerf.CurrentValue;
            }
            else
            {
                sectorAllocation[sector] = holdingPerf.CurrentValue;
            }
        }

        private void CalculatePortfolioTotals(PortfolioPerformance performance, Dictionary<string, decimal> sectorAllocation)
        {
            performance.TotalPL = performance.CurrentValue - performance.TotalInvestment;
            performance.TotalPLPercent = performance.TotalInvestment > 0
                ? (performance.TotalPL / performance.TotalInvestment) * 100
                : 0;

            // Convert sector allocation to percentages
            if (performance.CurrentValue > 0)
            {
                performance.SectorAllocation = sectorAllocation.ToDictionary(
                    kv => kv.Key,
                    kv => (kv.Value / performance.CurrentValue) * 100
                );
            }
        }

        public async Task<List<UserPortfolio>> GetAllPortfoliosAsync()
        {
            var portfolios = new List<UserPortfolio>();

            try
            {
                if (!Directory.Exists(_dataPath))
                {
                    _logger.LogDebug("Portfolio directory does not exist");
                    return portfolios;
                }

                var files = Directory.GetFiles(_dataPath, $"*{FILE_EXTENSION}");
                _logger.LogDebug("Found {Count} portfolio files", files.Length);

                foreach (var file in files)
                {
                    try
                    {
                        await _fileLock.WaitAsync();
                        try
                        {
                            var json = await File.ReadAllTextAsync(file);
                            var portfolio = JsonSerializer.Deserialize<UserPortfolio>(json);

                            if (portfolio?.Holdings != null)
                            {
                                portfolios.Add(portfolio);
                                _logger.LogTrace("Loaded portfolio for chat {ChatId} with {Count} holdings",
                                    portfolio.ChatId, portfolio.Holdings.Count);
                            }
                        }
                        finally
                        {
                            _fileLock.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error loading portfolio file {File}", file);
                    }
                }

                _logger.LogInformation("Successfully loaded {Count} portfolios", portfolios.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading all portfolios");
            }

            return portfolios;
        }

        public async Task SavePortfolioAsync(UserPortfolio portfolio)
        {
            if (portfolio == null)
            {
                _logger.LogError("Attempted to save null portfolio");
                return;
            }

            if (portfolio.ChatId <= 0)
            {
                _logger.LogError("Attempted to save portfolio with invalid chat ID");
                return;
            }

            try
            {
                var filePath = GetFilePath(portfolio.ChatId);
                var json = JsonSerializer.Serialize(portfolio, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                await _fileLock.WaitAsync();
                try
                {
                    await File.WriteAllTextAsync(filePath, json);
                    _logger.LogDebug("Saved portfolio for chat {ChatId} with {Count} holdings",
                        portfolio.ChatId, portfolio.Holdings?.Count ?? 0);
                }
                finally
                {
                    _fileLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving portfolio for chat {ChatId}", portfolio.ChatId);
                throw;
            }
        }

        private string GetFilePath(long chatId)
        {
            return Path.Combine(_dataPath, $"{chatId}{FILE_EXTENSION}");
        }

        public async Task<bool> DeletePortfolioAsync(long chatId)
        {
            if (chatId <= 0)
            {
                _logger.LogWarning("Invalid chat ID for portfolio deletion");
                return false;
            }

            try
            {
                var filePath = GetFilePath(chatId);
                if (File.Exists(filePath))
                {
                    await _fileLock.WaitAsync();
                    try
                    {
                        File.Delete(filePath);
                    }
                    finally
                    {
                        _fileLock.Release();
                    }

                    // Remove from cache
                    _portfolioCache.TryRemove(chatId, out _);

                    _logger.LogInformation("Deleted portfolio for chat {ChatId}", chatId);
                    return true;
                }

                _logger.LogDebug("Portfolio not found for chat {ChatId}", chatId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting portfolio for chat {ChatId}", chatId);
                return false;
            }
        }

        public async Task<string> GetPortfolioSummaryAsync(long chatId)
        {
            var performance = await GetPortfolioPerformanceAsync(chatId);
            if (performance == null)
                return "No portfolio found.";

            var emoji = performance.TotalPL >= PROFIT_THRESHOLD ? "🟢" : "🔴";

            return $"{emoji} Portfolio Summary:\n" +
                   $"Total Investment: ₹{performance.TotalInvestment:N2}\n" +
                   $"Current Value: ₹{performance.CurrentValue:N2}\n" +
                   $"P&L: {emoji} ₹{performance.TotalPL:N2} ({performance.TotalPLPercent:F2}%)\n" +
                   $"Holdings: {performance.Holdings.Count}";
        }

        // Method to clear cache for specific user
        public void InvalidateCache(long chatId)
        {
            _portfolioCache.TryRemove(chatId, out _);
            _logger.LogDebug("Invalidated cache for user {ChatId}", chatId);
        }

        // Method to clear all cache
        public void ClearAllCache()
        {
            _portfolioCache.Clear();
            _logger.LogInformation("Cleared all portfolio cache");
        }

        private class CachedPortfolio
        {
            public UserPortfolio Portfolio { get; set; } = null!;
            public DateTime Expiry { get; set; }
        }
    }
}