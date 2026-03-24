using Microsoft.Extensions.Logging;
using StockNotificationApi.Constants;
using StockNotificationApi.Interfaces;
using StockNotificationApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockNotificationApi.Services
{
    public class PortfolioAnalyzer : IPortfolioAnalyzer
    {
        private readonly ILogger<PortfolioAnalyzer> _logger;

        // Use constants from Constants.cs
        private const int HEALTH_SCORE_BASE = PortfolioConstants.HEALTH_SCORE_BASE;

        public PortfolioAnalyzer(ILogger<PortfolioAnalyzer> logger)
        {
            _logger = logger;
        }

        public int CalculateEnhancedHealthScore(List<Holding> holdings, decimal totalValue, decimal totalInvestment, Dictionary<string, decimal> sectorExposure)
        {
            if (holdings == null || !holdings.Any())
                return 0;

            int score = HEALTH_SCORE_BASE;
            var totalPL = totalValue - totalInvestment;
            var plPercentage = totalInvestment > 0 ? (totalPL / totalInvestment) * 100 : 0;

            if (plPercentage > 20)
                score += 20;
            else if (plPercentage > 10)
                score += 15;
            else if (plPercentage > 0)
                score += 10;
            else if (plPercentage > -10)
                score -= 5;
            else if (plPercentage > -20)
                score -= 15;
            else
                score -= 25;

            if (holdings.Count >= 8 && holdings.Count <= 15)
                score += 15;
            else if (holdings.Count >= 5 && holdings.Count < 8)
                score += 10;
            else if (holdings.Count > 15 && holdings.Count <= 20)
                score += 5;
            else if (holdings.Count < 5)
                score -= 10;
            else if (holdings.Count > 20)
                score -= 5;

            foreach (var holding in holdings)
            {
                var percentage = totalValue > 0 ? (holding.Quantity * holding.CurrentPrice / totalValue) * 100 : 0;
                if (percentage > 30)
                    score -= 15;
                else if (percentage > 20)
                    score -= 10;
                else if (percentage > 15)
                    score -= 5;
            }

            var losers = holdings.Count(h => h.ProfitLoss < 0);
            var loserRatio = holdings.Count > 0 ? (double)losers / holdings.Count : 0;

            if (loserRatio < 0.2)
                score += 15;
            else if (loserRatio < 0.4)
                score += 10;
            else if (loserRatio < 0.6)
                score += 5;
            else if (loserRatio > 0.8)
                score -= 15;
            else if (loserRatio > 0.6)
                score -= 10;

            if (sectorExposure != null)
            {
                if (sectorExposure.Count >= 5)
                    score += 10;
                else if (sectorExposure.Count >= 3)
                    score += 5;
                else if (sectorExposure.Count <= 1)
                    score -= 10;
            }

            return Math.Max(0, Math.Min(100, score));
        }

        public List<string> GenerateEnhancedWarnings(List<Holding> holdings, PortfolioSummary portfolio, Dictionary<string, decimal> sectorExposure)
        {
            var warnings = new List<string>();

            if (holdings == null || !holdings.Any())
                return warnings;

            var totalValue = portfolio?.CurrentValue ?? 0;
            var totalInvestment = portfolio?.TotalInvestment ?? 0;
            var totalPL = totalValue - totalInvestment;
            var plPercentage = totalInvestment > 0 ? (totalPL / totalInvestment) * 100 : 0;

            // Use proper emojis
            if (plPercentage < -30)
                warnings.Add($"🔴 Portfolio down {plPercentage:F1}% - Critical loss situation");
            else if (plPercentage < -20)
                warnings.Add($"⚠️ Portfolio down {plPercentage:F1}% - Review all holdings");
            else if (plPercentage < -10)
                warnings.Add($"⚠️ Portfolio down {plPercentage:F1}% - Consider stop-loss strategy");

            foreach (var holding in holdings)
            {
                var percentage = totalValue > 0 ? (holding.Quantity * holding.CurrentPrice / totalValue) * 100 : 0;
                if (percentage > 40)
                    warnings.Add($"🔥 {holding.Symbol} is {percentage:F1}% of portfolio - Extreme concentration risk");
                else if (percentage > 25)
                    warnings.Add($"⚠️ {holding.Symbol} is {percentage:F1}% of portfolio - High concentration");
                else if (percentage > 15)
                    warnings.Add($"📊 {holding.Symbol} is {percentage:F1}% of portfolio");
            }

            var losers = holdings.Where(h => h.ProfitLoss < 0).ToList();
            if (losers.Any())
            {
                var worstLoser = losers.OrderBy(h => h.ProfitLossPercent).First();
                warnings.Add($"🔴 Worst: {worstLoser.Symbol} ({worstLoser.ProfitLossPercent:F1}% loss, ₹{Math.Abs(worstLoser.ProfitLoss):N2})");

                var bigLosers = losers.Where(h => h.ProfitLossPercent < -20).ToList();
                if (bigLosers.Count > 1)
                    warnings.Add($"⚠️ {bigLosers.Count} stocks with >20% loss - Consider cutting losses");
            }

            if (sectorExposure != null && sectorExposure.Any())
            {
                var overExposed = sectorExposure.Where(s => s.Value > 40).ToList();
                foreach (var sector in overExposed)
                    warnings.Add($"⚠️ {sector.Key} sector at {sector.Value:F1}% - High concentration");
            }

            return warnings.Distinct().Take(5).ToList();
        }

        // FIXED: CalculateMomentum method with proper emojis
        public string CalculateMomentum(List<Holding> holdings)
        {
            var winners = holdings.Count(h => h.ProfitLossPercent > 0);
            var losers = holdings.Count(h => h.ProfitLossPercent < 0);

            if (winners == 0 && losers == 0) return "⚪ Neutral";
            if (winners > losers * 1.5) return "🟢 Strong Bullish";
            if (winners > losers) return "🟢 Mildly Bullish";
            if (losers > winners * 1.5) return "🔴 Strong Bearish";
            if (losers > winners) return "🔴 Mildly Bearish";
            return "⚪ Neutral";
        }

        // FIXED: CalculateDiversificationScore method with proper emojis
        public string CalculateDiversificationScore(Dictionary<string, decimal> sectorExposure, int holdingCount)
        {
            if (sectorExposure == null) return "⚪ Poor";
            if (sectorExposure.Count <= 2 && holdingCount <= 3) return "🔴 Very Poor";
            if (sectorExposure.Count <= 2) return "🔴 Poor (too few sectors)";
            if (sectorExposure.Count >= 5 && holdingCount >= 8) return "🟢 Excellent";
            if (sectorExposure.Count >= 4 && holdingCount >= 6) return "🟢 Good";
            if (sectorExposure.Count >= 3) return "🟡 Moderate";
            return "⚪ Poor";
        }

        // FIXED: EstimateRecoveryTime method with proper emojis
        public string EstimateRecoveryTime(decimal lossPercent)
        {
            if (lossPercent >= 0) return "N/A (In profit)";
            if (lossPercent > -10) return "🟡 3-6 months";
            if (lossPercent > -20) return "🟠 6-12 months";
            if (lossPercent > -30) return "🔴 1-2 years";
            if (lossPercent > -40) return "🔴 2-3 years";
            if (lossPercent > -50) return "🔴 3-4 years";
            return "⛔ 5+ years";
        }

        // FIXED: GenerateHeatMap method with proper emojis
        public string GenerateHeatMap(decimal percentage)
        {
            if (percentage > 50) return "🔥🔥 Extreme";
            if (percentage > 40) return "🔥🔥 Very High";
            if (percentage > 30) return "🔥 High";
            if (percentage > 25) return "🔥 Moderate-High";
            if (percentage > 20) return "🟡 Moderate";
            if (percentage > 15) return "🟡 Moderate";
            if (percentage > 10) return "🟢 Medium";
            if (percentage > 5) return "🟢 Low";
            return "⚪ Minimal";
        }
    }
}