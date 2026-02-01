namespace Lidarr.Plugin.Common.Services.Download
{
    public interface IDownloadTelemetrySink
    {
        void OnTrackCompleted(DownloadTelemetry telemetry);
    }
}

