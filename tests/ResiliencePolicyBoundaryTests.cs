using System;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class ResiliencePolicyBoundaryTests
    {
        [Theory]
        [InlineData(0, long.MaxValue, 1L)]
        [InlineData(-1, long.MaxValue, 1L)]
        [InlineData(int.MinValue, long.MaxValue, 1L)]
        [InlineData(63, long.MaxValue, 4611686018427387904L)]
        [InlineData(64, long.MaxValue, long.MaxValue)]
        [InlineData(2, 3L, 2L)]
        [InlineData(3, 3L, 3L)]
        public void Should_ReturnExactTicks_AtBackoffBoundaries(int attempt, long capTicks, long expectedTicks)
        {
            var policy = ResiliencePolicy.Default.With(
                initialBackoff: TimeSpan.FromTicks(1),
                maxBackoff: TimeSpan.FromTicks(capTicks));

            Assert.Equal(expectedTicks, policy.ComputeDelay(attempt).Ticks);
        }

        [Theory]
        [InlineData(1, 100)]
        [InlineData(2, 200)]
        [InlineData(3, 400)]
        public void Should_DoubleBackoff_BeforeCap(int attempt, int expectedMilliseconds)
        {
            var policy = ResiliencePolicy.Default.With(
                initialBackoff: TimeSpan.FromMilliseconds(100),
                maxBackoff: TimeSpan.FromSeconds(1));

            Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), policy.ComputeDelay(attempt));
        }

        [Fact]
        public void Should_ReturnCap_ForMaxValueAttempt()
        {
            var policy = ResiliencePolicy.Default.With(
                initialBackoff: TimeSpan.FromMilliseconds(100),
                maxBackoff: TimeSpan.FromSeconds(1));

            Assert.Equal(TimeSpan.FromSeconds(1), policy.ComputeDelay(int.MaxValue));
        }

        [Fact]
        public void Should_ReturnHugeCap_WithoutOverflowingBeforeCapping()
        {
            var cap = TimeSpan.FromDays(1000000);
            var policy = ResiliencePolicy.Default.With(initialBackoff: cap, maxBackoff: cap);

            // Attempt 8 would exceed TimeSpan's range if multiplied before capping.
            Assert.Equal(cap, policy.ComputeDelay(8));
        }
    }
}
