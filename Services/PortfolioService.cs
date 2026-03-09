// Services/PortfolioService.cs
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

        public PortfolioService(
            ILogger<PortfolioService> logger,
            IStockService stockService,
            IWebHostEnvironment env)
        {
            _logger = logger;
            _stockService = stockService;
            _dataPath = Path.Combine(env.ContentRootPath, "portfolio_data");

            // Create directory if it doesn't exist
            if (!Directory.Exists(_dataPath))
            {
                Directory.CreateDirectory(_dataPath);
            }
        }

        public async Task<UserPortfolio?> GetUserPortfolioAsync(long chatId)
        {
            var filePath = Path.Combine(_dataPath, $"{chatId}.json");

            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<UserPortfolio>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading portfolio for chat {ChatId}", chatId);
                return null;
            }
        }

        public async Task<bool> AddHoldingAsync(long chatId, string username, PortfolioHolding holding)
        {
            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId) ?? new UserPortfolio
                {
                    ChatId = chatId,
                    Username = username
                };

                // Check if holding already exists
                var existing = portfolio.Holdings.FirstOrDefault(h => h.Symbol == holding.Symbol);
                if (existing != null)
                {
                    // Update existing holding
                    existing.Quantity += holding.Quantity;
                    // Average buy price
                    existing.BuyPrice = ((existing.BuyPrice * existing.Quantity) + (holding.BuyPrice * holding.Quantity)) /
                                      (existing.Quantity + holding.Quantity);
                }
                else
                {
                    portfolio.Holdings.Add(holding);
                }

                portfolio.LastUpdated = DateTime.UtcNow;
                await SavePortfolioAsync(portfolio);

                _logger.LogInformation("Added holding {Symbol} for user {ChatId}", holding.Symbol, chatId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding holding for chat {ChatId}", chatId);
                return false;
            }
        }

        public async Task<bool> RemoveHoldingAsync(long chatId, string symbol)
        {
            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId);
                if (portfolio == null) return false;

                var holding = portfolio.Holdings.FirstOrDefault(h => h.Symbol == symbol);
                if (holding == null) return false;

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
            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId);
                if (portfolio == null) return false;

                var existing = portfolio.Holdings.FirstOrDefault(h => h.Symbol == holding.Symbol);
                if (existing == null) return false;

                existing.Quantity = holding.Quantity;
                existing.BuyPrice = holding.BuyPrice;
                existing.Notes = holding.Notes;
                portfolio.LastUpdated = DateTime.UtcNow;

                await SavePortfolioAsync(portfolio);
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
            try
            {
                var portfolio = await GetUserPortfolioAsync(chatId);
                if (portfolio == null || !portfolio.Holdings.Any())
                {
                    return null;
                }

                var performance = new PortfolioPerformance();
                var sectorAllocation = new Dictionary<string, decimal>();

                foreach (var holding in portfolio.Holdings)
                {
                    // Get current stock price
                    var stock = await _stockService.GetStockDataAsync($"{holding.Symbol}.NS");
                    if (stock == null)
                    {
                        stock = await _stockService.GetStockDataAsync($"{holding.Symbol}.BO");
                    }

                    if (stock != null)
                    {
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
                        performance.TodayPL += holdingPerf.Quantity * stock.Change;

                        // Sector allocation
                        var sector = stock.Sector ?? "Other";
                        if (sectorAllocation.ContainsKey(sector))
                        {
                            sectorAllocation[sector] += holdingPerf.CurrentValue;
                        }
                        else
                        {
                            sectorAllocation[sector] = holdingPerf.CurrentValue;
                        }
                    }
                }

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

                return performance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating portfolio performance for chat {ChatId}", chatId);
                return null;
            }
        }

        public async Task<List<UserPortfolio>> GetAllPortfoliosAsync()
        {
            var portfolios = new List<UserPortfolio>();

            if (!Directory.Exists(_dataPath))
            {
                return portfolios;
            }

            var files = Directory.GetFiles(_dataPath, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var portfolio = JsonSerializer.Deserialize<UserPortfolio>(json);
                    if (portfolio != null)
                    {
                        portfolios.Add(portfolio);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading portfolio file {File}", file);
                }
            }

            return portfolios;
        }

        public async Task SavePortfolioAsync(UserPortfolio portfolio)
        {
            lock (_lockObject)
            {
                var filePath = Path.Combine(_dataPath, $"{portfolio.ChatId}.json");
                var json = JsonSerializer.Serialize(portfolio, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            await Task.CompletedTask;
        }
    }
}