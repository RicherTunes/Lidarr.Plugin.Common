using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// Tests for the Phase 5e once-loggers (LogWarningOnce, LogInformationOnce, LogErrorOnce).
/// Verifies: same key emits once; different keys all emit; ResetOnceKeys clears between tests.
/// </summary>
[Trait("Category", "Unit")]
[Collection("LoggerExtensionsOnce")] // serialize: shared static seen-key set
public class LoggerExtensionsOnceTests : IDisposable
{
    public LoggerExtensionsOnceTests()
    {
        // Clear any state left by other tests so cases are deterministic.
        Lidarr.Plugin.Common.Observability.LoggerExtensions.ResetOnceKeys();
    }

    public void Dispose()
    {
        Lidarr.Plugin.Common.Observability.LoggerExtensions.ResetOnceKeys();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, formatter(state, exception)));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void LogWarningOnce_EmitsOnFirstCall_SuppressesOnSubsequent()
    {
        var logger = new CapturingLogger();
        var emitted1 = logger.LogWarningOnce("k1", new EventId(1), "warn {V}", 42);
        var emitted2 = logger.LogWarningOnce("k1", new EventId(1), "warn {V}", 42);

        Assert.True(emitted1);
        Assert.False(emitted2);
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logger.Entries[0].Level);
        Assert.Contains("warn 42", logger.Entries[0].Message);
    }

    [Fact]
    public void LogWarningOnce_DifferentKeys_AllEmit()
    {
        var logger = new CapturingLogger();
        Assert.True(logger.LogWarningOnce("a", "msg-a"));
        Assert.True(logger.LogWarningOnce("b", "msg-b"));
        Assert.True(logger.LogWarningOnce("c", "msg-c"));

        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void LogInformationOnce_EmitsOnce()
    {
        var logger = new CapturingLogger();
        Assert.True(logger.LogInformationOnce("info-k", new EventId(2), "hello"));
        Assert.False(logger.LogInformationOnce("info-k", new EventId(2), "hello"));
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
    }

    [Fact]
    public void LogErrorOnce_EmitsOnce_CarriesExceptionAndMessage()
    {
        var logger = new CapturingLogger();
        var ex = new InvalidOperationException("boom");
        Assert.True(logger.LogErrorOnce("err-k", new EventId(3), ex, "failed: {Why}", "boom"));
        Assert.False(logger.LogErrorOnce("err-k", new EventId(3), ex, "failed: {Why}", "boom"));

        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logger.Entries[0].Level);
        Assert.Contains("failed: boom", logger.Entries[0].Message);
    }

    [Fact]
    public void ResetOnceKeys_ClearsBetweenInvocations()
    {
        var logger = new CapturingLogger();
        Assert.True(logger.LogWarningOnce("z", "first"));
        Assert.False(logger.LogWarningOnce("z", "second"));

        Lidarr.Plugin.Common.Observability.LoggerExtensions.ResetOnceKeys();

        Assert.True(logger.LogWarningOnce("z", "after-reset"));
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void NullLogger_StillRespectsKeyDeduplication()
    {
        // Even with NullLogger, the "once" gate fires on the first call and suppresses thereafter.
        // This matters because plugins may use NullLogger for tests or fallback paths.
        Assert.True(NullLogger.Instance.LogWarningOnce("null-k", "ignored"));
        Assert.False(NullLogger.Instance.LogWarningOnce("null-k", "ignored"));
    }

    [Fact]
    public async Task ConcurrentCallers_SameKey_OnlyOneEmits()
    {
        var logger = new CapturingLogger();
        var key = "concurrent-key";
        var trueCount = 0;

        var tasks = new Task[100];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                if (logger.LogWarningOnce(key, "msg"))
                {
                    System.Threading.Interlocked.Increment(ref trueCount);
                }
            });
        }
        await Task.WhenAll(tasks);

        Assert.Equal(1, trueCount);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void NullKey_Throws()
    {
        var logger = new CapturingLogger();
        Assert.Throws<ArgumentException>(() => logger.LogWarningOnce(string.Empty, "msg"));
    }

    [Fact]
    public void NullLoggerInstance_Throws()
    {
        ILogger? logger = null;
        Assert.Throws<ArgumentNullException>(() => logger!.LogWarningOnce("k", "msg"));
    }
}
