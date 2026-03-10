using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System.Text.Json;

namespace StockNotificationApi.Services
{
    public class PortfolioService : IPortfolioService
    {
        private readonly ILogger<PortfolioService> _logger;
        private readonly IStockService _stockService;
        private readonly string _dataPath;
        private readonly object _lockObject = new();

        // Constants
        private const string DATA_DIRECTORY = "portfolio_data";
        private const string FILE_EXTENSION = ".json";
        private const decimal PROFIT_THRESHOLD = 0m;
        private const int MAX_HOLDINGS_PER_USER = 50;
        private const int MIN_QUANTITY = 1;
        private const decimal MIN_PRICE = 0.01m;

        public PortfolioService(
            ILogger<PortfolioService> logger,
            IStockService stockService,
            IWebHostEnvironment env)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));

            if (env == null) throw new ArgumentNullException(nameof(env));

            _dataPath = Path.Combine(env.ContentRootPath, DATA_DIRECTORY);

            // Create directory if it doesn't exist
            EnsureDirectoryExists();
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

        public async Task<UserPortfolio?> GetUserPortfolioAsync(long chatId)
        {
            if (chatId <= 0)
            {
                _logger.LogWarning("Invalid chat ID: {ChatId}", chatId);
                return null;
            }

            var filePath = GetFilePath(chatId);

            if (!File.Exists(filePath))
            {
                _logger.LogDebug("No portfolio found for chat {ChatId}", chatId);
                return null;
            }

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

                var performance = new PortfolioPerformance
                {
                    Holdings = new List<HoldingPerformance>(),
                    SectorAllocation = new Dictionary<string, decimal>()
                };

                var sectorAllocation = new Dictionary<string, decimal>();

                foreach (var holding in portfolio.Holdings.Where(h => h != null))
                {
                    var holdingPerf = await CalculateHoldingPerformance(holding);
                    if (holdingPerf != null)
                    {
                        performance.Holdings.Add(holdingPerf);
                        performance.TotalInvestment += holdingPerf.Investment;
                        performance.CurrentValue += holdingPerf.CurrentValue;
                        performance.TodayPL += holdingPerf.Quantity * holdingPerf.DayChange;

                        UpdateSectorAllocation(sectorAllocation, holdingPerf);
                    }
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

        private async Task<HoldingPerformance?> CalculateHoldingPerformance(PortfolioHolding holding)
        {
            try
            {
                // Get current stock price
                var stock = await _stockService.GetStockDataAsync($"{holding.Symbol}.NS");
                if (stock == null)
                {
                    stock = await _stockService.GetStockDataAsync($"{holding.Symbol}.BO");
                }

                if (stock == null)
                {
                    _logger.LogWarning("Could not fetch current price for {Symbol}", holding.Symbol);
                    return null;
                }

                return new HoldingPerformance
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating performance for {Symbol}", holding.Symbol);
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
                        var json = await File.ReadAllTextAsync(file);
                        var portfolio = JsonSerializer.Deserialize<UserPortfolio>(json);

                        if (portfolio?.Holdings != null)
                        {
                            portfolios.Add(portfolio);
                            _logger.LogTrace("Loaded portfolio for chat {ChatId} with {Count} holdings",
                                portfolio.ChatId, portfolio.Holdings.Count);
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

                lock (_lockObject)
                {
                    File.WriteAllText(filePath, json);
                }

                _logger.LogDebug("Saved portfolio for chat {ChatId} with {Count} holdings",
                    portfolio.ChatId, portfolio.Holdings?.Count ?? 0);

                await Task.CompletedTask;
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

        // Optional: Method to delete portfolio
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
                    lock (_lockObject)
                    {
                        File.Delete(filePath);
                    }
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

        // Optional: Method to get portfolio summary
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
    }
}