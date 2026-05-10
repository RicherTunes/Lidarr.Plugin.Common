using System;
using Lidarr.Plugin.Common.Services.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Resilience;

public class DefensiveServiceWrapperTests
{
    private interface ITargetService
    {
        int Compute(int x);
    }

    private sealed class FlakyService : ITargetService
    {
        public int CallCount { get; private set; }
        public bool ShouldThrow { get; set; }
        public int Compute(int x)
        {
            CallCount++;
            if (ShouldThrow) throw new InvalidOperationException("boom");
            return x * 2;
        }
    }

    private static DefensiveServiceWrapper<ITargetService> CreateWrapper(ITargetService svc)
        => new(svc, NullLogger<DefensiveServiceWrapper<ITargetService>>.Instance);

    [Fact]
    public void Ctor_NullService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DefensiveServiceWrapper<ITargetService>(
            null!, NullLogger<DefensiveServiceWrapper<ITargetService>>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DefensiveServiceWrapper<ITargetService>(new FlakyService(), null!));
    }

    [Fact]
    public void ExecuteSafely_Success_ReturnsResultAndKeepsHealthy()
    {
        var svc = new FlakyService();
        var wrapper = CreateWrapper(svc);

        var result = wrapper.ExecuteSafely(s => s.Compute(21), -1);

        Assert.Equal(42, result);
        Assert.True(wrapper.IsHealthy);
        Assert.Equal(0, wrapper.ConsecutiveFailures);
    }

    [Fact]
    public void ExecuteSafely_Exception_ReturnsFallbackAndIncrementsFailures()
    {
        var svc = new FlakyService { ShouldThrow = true };
        var wrapper = CreateWrapper(svc);

        var result = wrapper.ExecuteSafely(s => s.Compute(21), fallbackValue: -1);

        Assert.Equal(-1, result);
        Assert.True(wrapper.IsHealthy);                  // not broken yet (1 < 5)
        Assert.Equal(1, wrapper.ConsecutiveFailures);
    }

    [Fact]
    public void ExecuteSafely_FiveConsecutiveFailures_OpensCircuit()
    {
        var svc = new FlakyService { ShouldThrow = true };
        var wrapper = CreateWrapper(svc);

        for (var i = 0; i < 5; i++)
        {
            wrapper.ExecuteSafely(s => s.Compute(1), -1);
        }

        Assert.False(wrapper.IsHealthy);
        Assert.Equal(5, wrapper.ConsecutiveFailures);
        Assert.Equal(5, svc.CallCount);                  // service was called 5 times before circuit opened
    }

    [Fact]
    public void ExecuteSafely_AfterCircuitOpens_ShortCircuitsWithoutCallingService()
    {
        var svc = new FlakyService { ShouldThrow = true };
        var wrapper = CreateWrapper(svc);

        for (var i = 0; i < 5; i++)
        {
            wrapper.ExecuteSafely(s => s.Compute(1), -1);
        }
        var callCountAtOpen = svc.CallCount;

        // Subsequent calls must not reach the service.
        wrapper.ExecuteSafely(s => s.Compute(1), fallbackValue: -1);
        wrapper.ExecuteSafely(s => s.Compute(1), fallbackValue: -1);

        Assert.Equal(callCountAtOpen, svc.CallCount);
        Assert.False(wrapper.IsHealthy);
    }

    [Fact]
    public void ExecuteSafely_SuccessAfterFailures_ResetsConsecutiveCounter()
    {
        var svc = new FlakyService();
        var wrapper = CreateWrapper(svc);

        // Three failures, then a success — counter should reset to zero.
        svc.ShouldThrow = true;
        for (var i = 0; i < 3; i++) wrapper.ExecuteSafely(s => s.Compute(1), -1);
        Assert.Equal(3, wrapper.ConsecutiveFailures);

        svc.ShouldThrow = false;
        var result = wrapper.ExecuteSafely(s => s.Compute(5), -1);

        Assert.Equal(10, result);
        Assert.Equal(0, wrapper.ConsecutiveFailures);
        Assert.True(wrapper.IsHealthy);
    }

    [Fact]
    public void ResetHealth_AfterCircuitOpen_RestoresService()
    {
        var svc = new FlakyService { ShouldThrow = true };
        var wrapper = CreateWrapper(svc);
        for (var i = 0; i < 5; i++) wrapper.ExecuteSafely(s => s.Compute(1), -1);
        Assert.False(wrapper.IsHealthy);

        wrapper.ResetHealth();
        Assert.True(wrapper.IsHealthy);
        Assert.Equal(0, wrapper.ConsecutiveFailures);

        svc.ShouldThrow = false;
        var result = wrapper.ExecuteSafely(s => s.Compute(7), -1);
        Assert.Equal(14, result);
    }

    [Fact]
    public void ExecuteSafely_VoidOverload_RunsActionOnSuccess()
    {
        var svc = new FlakyService();
        var wrapper = CreateWrapper(svc);
        var observed = 0;

        wrapper.ExecuteSafely(s => observed = s.Compute(3));

        Assert.Equal(6, observed);
        Assert.True(wrapper.IsHealthy);
    }

    [Fact]
    public void ExecuteSafely_VoidOverload_SwallowsExceptionAndCountsFailure()
    {
        var svc = new FlakyService { ShouldThrow = true };
        var wrapper = CreateWrapper(svc);

        wrapper.ExecuteSafely(s => s.Compute(3));        // must not throw

        Assert.Equal(1, wrapper.ConsecutiveFailures);
    }

    [Fact]
    public void DefensiveService_Wrap_BuildsConfiguredWrapper()
    {
        var svc = new FlakyService();
        var wrapper = DefensiveService.Wrap<ITargetService>(
            svc,
            NullLogger<DefensiveServiceWrapper<ITargetService>>.Instance);

        Assert.NotNull(wrapper);
        Assert.True(wrapper.IsHealthy);
        var result = wrapper.ExecuteSafely(s => s.Compute(11), -1);
        Assert.Equal(22, result);
    }
}
