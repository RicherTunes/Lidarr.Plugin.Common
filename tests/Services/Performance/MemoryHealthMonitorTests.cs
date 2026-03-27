using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Lidarr.Plugin.Common.Services.Performance.MemoryHealthMonitor;

namespace Lidarr.Plugin.Common.Tests.Services.Performance
{
    [Trait("Category", "Unit")]
    public class MemoryHealthMonitorTests
    {
        private static ILogger<MemoryHealthMonitor> CreateLogger() =>
            NullLoggerFactory.Instance.CreateLogger<MemoryHealthMonitor>();

        #region Constructor / Factory Validation

        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MemoryHealthMonitor(logger: null!));
        }

        [Fact]
        public void Constructor_DefaultThresholds_Succeeds()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var stats = monitor.GetCurrentStatistics();
            Assert.Equal(MemoryHealthStatus.Healthy, stats.Status);
        }

        [Fact]
        public void Constructor_CustomThresholds_Succeeds()
        {
            using var monitor = new MemoryHealthMonitor(
                CreateLogger(),
                warningThresholdMB: 256,
                criticalThresholdMB: 512,
                monitorInterval: TimeSpan.FromMinutes(1));

            var stats = monitor.GetCurrentStatistics();
            Assert.NotNull(stats);
        }

        #endregion

        #region GetCurrentStatistics

        [Fact]
        public void GetCurrentStatistics_ReturnsNonNullResult()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var stats = monitor.GetCurrentStatistics();

            Assert.NotNull(stats);
            Assert.True(stats.LastChecked <= DateTime.UtcNow);
        }

        [Fact]
        public void GetCurrentStatistics_ReturnsClone()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var stats1 = monitor.GetCurrentStatistics();
            var stats2 = monitor.GetCurrentStatistics();

            Assert.NotSame(stats1, stats2);
        }

        [Fact]
        public void GetCurrentStatistics_InitialStatus_IsHealthy()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var stats = monitor.GetCurrentStatistics();

            Assert.Equal(MemoryHealthStatus.Healthy, stats.Status);
            // TrendDirection may vary depending on whether the timer callback has fired,
            // so just verify it is a valid enum value.
            Assert.True(Enum.IsDefined(typeof(MemoryTrend), stats.TrendDirection));
        }

        #endregion

        #region GetOptimizationAdvice

        [Fact]
        public void GetOptimizationAdvice_WhenHealthy_ReturnsNoOptimization()
        {
            using var monitor = new MemoryHealthMonitor(
                CreateLogger(),
                warningThresholdMB: long.MaxValue / 2,
                criticalThresholdMB: long.MaxValue / 2);

            var advice = monitor.GetOptimizationAdvice();

            Assert.Equal(MemoryHealthStatus.Healthy, advice.CurrentStatus);
            Assert.False(advice.ShouldOptimize);
            Assert.Equal(OptimizationUrgency.None, advice.Urgency);
            Assert.Contains("healthy", advice.Recommendation, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GetOptimizationAdvice_PopulatesMemoryMetrics()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var advice = monitor.GetOptimizationAdvice();

            // WorkingSetMB and ManagedMemoryMB come from GetCurrentStatistics
            // At startup they are 0 (before first timer tick), so just verify they are non-negative
            Assert.True(advice.WorkingSetMB >= 0);
            Assert.True(advice.ManagedMemoryMB >= 0);
        }

        [Fact]
        public void GetOptimizationAdvice_InitialState_NoMemoryLeak()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var advice = monitor.GetOptimizationAdvice();

            Assert.False(advice.PossibleMemoryLeak);
            Assert.False(advice.IsGrowthConcerning);
        }

        #endregion

        #region OptimizeMemoryAsync

        [Fact]
        public async Task OptimizeMemoryAsync_Standard_ReturnsSuccessResult()
        {
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var result = await monitor.OptimizeMemoryAsync(aggressive: false);

            Assert.True(result.Success);
            Assert.Equal("Standard", result.OptimizationType);
            Assert.Null(result.ErrorMessage);
            Assert.True(result.EndTime >= result.StartTime);
            Assert.True(result.MemoryFreedMB >= 0);
            Assert.True(result.Duration >= TimeSpan.Zero);
        }

        [Fact]
        public async Task OptimizeMemoryAsync_Aggressive_ReturnsResult()
        {
            // With healthy status, aggressive mode still uses standard path
            using var monitor = new MemoryHealthMonitor(CreateLogger());

            var result = await monitor.OptimizeMemoryAsync(aggressive: true);

            Assert.True(result.Success);
            Assert.True(result.StartMemoryMB >= 0);
        }

        #endregion

        #region MemoryHealthStatistics Clone

        [Fact]
        public void MemoryHealthStatistics_Clone_CopiesAllFields()
        {
            var original = new MemoryHealthStatistics
            {
                Status = MemoryHealthStatus.Warning,
                WorkingSetMB = 600,
                ManagedMemoryMB = 400,
                PrivateMemoryMB = 700,
                Gen0Collections = 10,
                Gen1Collections = 5,
                Gen2Collections = 2,
                LastChecked = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TrendDirection = MemoryTrend.Increasing
            };

            var clone = original.Clone();

            Assert.NotSame(original, clone);
            Assert.Equal(original.Status, clone.Status);
            Assert.Equal(original.WorkingSetMB, clone.WorkingSetMB);
            Assert.Equal(original.ManagedMemoryMB, clone.ManagedMemoryMB);
            Assert.Equal(original.PrivateMemoryMB, clone.PrivateMemoryMB);
            Assert.Equal(original.Gen0Collections, clone.Gen0Collections);
            Assert.Equal(original.Gen1Collections, clone.Gen1Collections);
            Assert.Equal(original.Gen2Collections, clone.Gen2Collections);
            Assert.Equal(original.LastChecked, clone.LastChecked);
            Assert.Equal(original.TrendDirection, clone.TrendDirection);
        }

        #endregion

        #region Disposal

        [Fact]
        public void Dispose_CanBeCalledOnce()
        {
            var monitor = new MemoryHealthMonitor(CreateLogger());

            // Should not throw
            monitor.Dispose();
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            var monitor = new MemoryHealthMonitor(CreateLogger());

            monitor.Dispose();
            monitor.Dispose();
            monitor.Dispose();
        }

        [Fact]
        public void Dispose_AfterDispose_GetCurrentStatistics_StillWorks()
        {
            // MemoryHealthMonitor does not gate reads on disposal — it only stops the timer.
            var monitor = new MemoryHealthMonitor(CreateLogger());
            monitor.Dispose();

            var stats = monitor.GetCurrentStatistics();
            Assert.NotNull(stats);
        }

        #endregion

        #region Enum Value Coverage

        [Theory]
        [InlineData(MemoryHealthStatus.Healthy)]
        [InlineData(MemoryHealthStatus.Warning)]
        [InlineData(MemoryHealthStatus.Critical)]
        public void MemoryHealthStatus_AllValuesAreDefined(MemoryHealthStatus status)
        {
            Assert.True(Enum.IsDefined(typeof(MemoryHealthStatus), status));
        }

        [Theory]
        [InlineData(MemoryTrend.Decreasing)]
        [InlineData(MemoryTrend.Stable)]
        [InlineData(MemoryTrend.Increasing)]
        public void MemoryTrend_AllValuesAreDefined(MemoryTrend trend)
        {
            Assert.True(Enum.IsDefined(typeof(MemoryTrend), trend));
        }

        [Theory]
        [InlineData(OptimizationUrgency.None)]
        [InlineData(OptimizationUrgency.Soon)]
        [InlineData(OptimizationUrgency.Immediate)]
        public void OptimizationUrgency_AllValuesAreDefined(OptimizationUrgency urgency)
        {
            Assert.True(Enum.IsDefined(typeof(OptimizationUrgency), urgency));
        }

        #endregion

        #region MemoryOptimizationResult

        [Fact]
        public void MemoryOptimizationResult_Duration_ComputedCorrectly()
        {
            var result = new MemoryOptimizationResult
            {
                StartTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 1, 12, 0, 5, DateTimeKind.Utc)
            };

            Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        }

        #endregion

        #region MemoryOptimizationAdvice Defaults

        [Fact]
        public void MemoryOptimizationAdvice_Defaults_AreReasonable()
        {
            var advice = new MemoryOptimizationAdvice();

            Assert.Equal(MemoryHealthStatus.Healthy, advice.CurrentStatus);
            Assert.Equal(0, advice.WorkingSetMB);
            Assert.Equal(0, advice.ManagedMemoryMB);
            Assert.False(advice.ShouldOptimize);
            Assert.Equal(OptimizationUrgency.None, advice.Urgency);
            Assert.Null(advice.Recommendation);
            Assert.Equal(0.0, advice.SuggestedBatchSizeReduction);
            Assert.Equal(0.0, advice.MemoryGrowthRate);
            Assert.False(advice.IsGrowthConcerning);
            Assert.False(advice.PossibleMemoryLeak);
        }

        #endregion
    }
}
