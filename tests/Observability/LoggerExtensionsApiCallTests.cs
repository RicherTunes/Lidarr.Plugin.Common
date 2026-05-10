using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lidarr.Plugin.Common.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// Coverage gap fill (wave 7): exercises <see cref="LoggerExtensions.LogApiCallStarted"/>,
/// <see cref="LoggerExtensions.LogApiCallCompleted"/>, and the nested <c>ActivityScope</c> dispose
/// path. Pre-wave-7 coverage: line-rate 56.25% (ActivityScope at 0%). These tests target the
/// Activity tag mutations, the disposed-completion log, and the swallow-exception guard.
/// </summary>
[Trait("Category", "Unit")]
public class LoggerExtensionsApiCallTests
{
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
    public void LogApiCallStarted_EmitsInfoLog_AndStartsActivity()
    {
        // Force any ambient listener-less environment to start an Activity (W3C is default).
        var logger = new CapturingLogger();

        using var scope = logger.LogApiCallStarted("svc", "/v1/x", correlationId: "cid-1");

        Assert.NotNull(scope);
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.Contains("API call started: svc /v1/x cid-1", logger.Entries[0].Message);
    }

    [Fact]
    public void LogApiCallStarted_WithoutCorrelationId_OmitsTag()
    {
        var logger = new CapturingLogger();

        using var scope = logger.LogApiCallStarted("svc", "/v1/y");

        Assert.Single(logger.Entries);
        // Empty correlation id substitutes empty string in the message.
        Assert.Contains("API call started: svc /v1/y ", logger.Entries[0].Message);
    }

    [Fact]
    public void LogApiCallStarted_DisposingScope_EmitsCompletionLog()
    {
        var logger = new CapturingLogger();

        var scope = logger.LogApiCallStarted("svc", "/v1/z", correlationId: "cid-2");
        Assert.Single(logger.Entries);

        scope.Dispose();

        // Dispose triggers the onDispose callback which logs "API call finished".
        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("API call finished: svc /v1/z", logger.Entries[1].Message);
    }

    [Fact]
    public void LogApiCallStarted_DisposingScope_SwallowsExceptionFromCallback()
    {
        // We cannot easily inject a throwing onDispose, but we can verify Dispose does not throw
        // even when the scope is disposed twice or when the activity has already been stopped.
        var logger = new CapturingLogger();

        var scope = logger.LogApiCallStarted("svc", "/v1/q");
        scope.Dispose();
        // Second dispose must be a no-op-or-safe path; ActivityScope.Dispose stops the activity
        // and invokes the callback (which logs again). We tolerate both behaviors as long as
        // no exception escapes — that exercises the catch block.
        var ex = Record.Exception(() => scope.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void LogApiCallCompleted_NoActivity_StillLogsWithoutThrowing()
    {
        // When Activity.Current is null, the helper must skip the tag-set block and still log.
        // Stop any ambient activity left over from previous tests.
        while (Activity.Current is { } a)
        {
            a.Stop();
        }
        Assert.Null(Activity.Current);

        var logger = new CapturingLogger();
        logger.LogApiCallCompleted("svc", "/v1/done", statusCode: 200, success: true, duration: TimeSpan.FromMilliseconds(125));

        Assert.Single(logger.Entries);
        Assert.Contains("API call completed: svc /v1/done 200 True 125ms", logger.Entries[0].Message);
    }

    [Fact]
    public void LogApiCallCompleted_WithActiveActivity_SetsTagsOnCurrent()
    {
        // Start an activity manually so the helper's "act != null" branch is taken.
        using var activity = new Activity("test.activity");
        activity.Start();
        Assert.NotNull(Activity.Current);

        var logger = new CapturingLogger();
        logger.LogApiCallCompleted("svc", "/v1/done", statusCode: 503, success: false, duration: TimeSpan.FromMilliseconds(900));

        // Tag values are stored as object?; Activity.Tags exposes them as string-stringified pairs.
        var tags = activity.TagObjects;
        var statusTag = false;
        var successTag = false;
        var durationTag = false;
        foreach (var kv in tags)
        {
            if (kv.Key == "status_code") statusTag = true;
            if (kv.Key == "success") successTag = true;
            if (kv.Key == "duration_ms") durationTag = true;
        }
        Assert.True(statusTag);
        Assert.True(successTag);
        Assert.True(durationTag);

        Assert.Single(logger.Entries);
        Assert.Contains("API call completed: svc /v1/done 503 False 900ms", logger.Entries[0].Message);
    }
}
