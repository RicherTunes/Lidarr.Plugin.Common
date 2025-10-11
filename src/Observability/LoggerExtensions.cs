using System;
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
