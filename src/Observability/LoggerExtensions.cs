using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Observability;

public static class LoggerExtensions
{
    public static IDisposable LogApiCallStarted(this ILogger logger, string service, string endpoint, string? correlationId = null)
    {
        var activity = new Activity("plugin.api");
        if (!string.IsNullOrEmpty(correlationId)) activity.SetTag("correlation_id", correlationId);
        activity.SetTag("service", service);
        activity.SetTag("endpoint", endpoint);
        activity.Start();
        logger.LogInformation(EventIds.ApiCallStarted, "API call started: {Service} {Endpoint} {CorrelationId}", service, endpoint, correlationId ?? string.Empty);
        return new ActivityScope(activity, () => logger.LogInformation(EventIds.ApiCallCompleted, "API call finished: {Service} {Endpoint}", service, endpoint));
    }

    public static void LogApiCallCompleted(this ILogger logger, string service, string endpoint, int statusCode, bool success, TimeSpan duration)
    {
        var act = Activity.Current;
        if (act != null)
        {
            act.SetTag("status_code", statusCode);
            act.SetTag("success", success);
            act.SetTag("duration_ms", (long)duration.TotalMilliseconds);
        }
        logger.LogInformation(EventIds.ApiCallCompleted, "API call completed: {Service} {Endpoint} {Status} {Success} {DurationMs}ms", service, endpoint, statusCode, success, (long)duration.TotalMilliseconds);
    }

    // ---- "Once" log helpers ----
    //
    // Keyed log emission: the same (key) emits once per process lifetime. Plugins use this for fallback /
    // configuration warnings that should be noisy on first occurrence but silent thereafter (e.g., "no
    // IHttpResilience registered, falling back to direct HttpClient" emitted from N provider sites).
    //
    // Source: brainarr Phase 4e ("brainarr-local under CorrelationContext.cs; used at 7 provider sites for
    // IHttpResilience fallback warnings").
    //
    // The seen-key set is process-scoped (a single ConcurrentDictionary). This intentionally avoids
    // attaching the seen-set to a specific ILogger instance: in practice the keys are descriptive strings
    // ("brainarr.fallback.IHttpResilience") that should suppress duplicates regardless of which logger
    // raised them. Tests can call ResetOnceKeys() to clear state.
    private static readonly ConcurrentDictionary<string, byte> SeenOnceKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Emits a warning the first time it is invoked with a given <paramref name="key"/>; subsequent calls
    /// with the same key are no-ops.
    /// </summary>
    /// <param name="logger">The logger to emit on.</param>
    /// <param name="key">
    /// Stable key identifying the warning. Same key from any caller suppresses subsequent emissions.
    /// </param>
    /// <param name="eventId">Event id attached to the emitted log entry.</param>
    /// <param name="message">Message template (passed verbatim to <see cref="ILogger.Log"/>).</param>
    /// <param name="args">Template arguments.</param>
    /// <returns><see langword="true"/> if the warning was emitted; <see langword="false"/> if suppressed.</returns>
    public static bool LogWarningOnce(this ILogger logger, string key, EventId eventId, string message, params object?[] args)
    {
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty.", nameof(key));
        if (!SeenOnceKeys.TryAdd(key, 0)) return false;
        logger.LogWarning(eventId, message, args);
        return true;
    }

    /// <summary>
    /// Convenience overload of <see cref="LogWarningOnce(ILogger, string, EventId, string, object?[])"/>
    /// that uses a default <see cref="EventId"/>.
    /// </summary>
    public static bool LogWarningOnce(this ILogger logger, string key, string message, params object?[] args)
        => LogWarningOnce(logger, key, default, message, args);

    /// <summary>
    /// Emits an info message the first time it is invoked with a given <paramref name="key"/>; subsequent
    /// calls with the same key are no-ops.
    /// </summary>
    public static bool LogInformationOnce(this ILogger logger, string key, EventId eventId, string message, params object?[] args)
    {
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty.", nameof(key));
        if (!SeenOnceKeys.TryAdd(key, 0)) return false;
        logger.LogInformation(eventId, message, args);
        return true;
    }

    /// <summary>
    /// Emits an error message the first time it is invoked with a given <paramref name="key"/>; subsequent
    /// calls with the same key are no-ops.
    /// </summary>
    public static bool LogErrorOnce(this ILogger logger, string key, EventId eventId, Exception? exception, string message, params object?[] args)
    {
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty.", nameof(key));
        if (!SeenOnceKeys.TryAdd(key, 0)) return false;
        logger.LogError(eventId, exception, message, args);
        return true;
    }

    /// <summary>
    /// Test-only helper: clears the process-wide set of "once" keys. Use in unit tests that need to
    /// reset state between cases.
    /// </summary>
    public static void ResetOnceKeys() => SeenOnceKeys.Clear();

    private sealed class ActivityScope : IDisposable
    {
        private readonly Activity _activity;
        private readonly Action _onDispose;
        public ActivityScope(Activity activity, Action onDispose)
        {
            _activity = activity;
            _onDispose = onDispose;
        }
        public void Dispose()
        {
            try { _onDispose(); } catch { }
            _activity.Stop();
        }
    }
}
