namespace Lidarr.Plugin.Common.Observability;

public static class EventIds
{
    public static readonly Microsoft.Extensions.Logging.EventId ApiCallStarted = new(1000, nameof(ApiCallStarted));
    public static readonly Microsoft.Extensions.Logging.EventId ApiCallCompleted = new(1001, nameof(ApiCallCompleted));
    public static readonly Microsoft.Extensions.Logging.EventId DownloadChunkCompleted = new(1002, nameof(DownloadChunkCompleted));
}
