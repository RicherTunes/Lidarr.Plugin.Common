namespace Lidarr.Plugin.Common.HostBridge;

public static class HostBridgeQueueStateMachine
{
    public static bool IsTerminal(HostBridgeDownloadAttemptState state) => state is
        HostBridgeDownloadAttemptState.CompletedImportable or
        HostBridgeDownloadAttemptState.Failed or
        HostBridgeDownloadAttemptState.Cancelled;

    public static bool CanTransition(
        HostBridgeDownloadAttemptState from,
        HostBridgeDownloadAttemptState to)
    {
        if (IsTerminal(from)) return false;
        if (to == HostBridgeDownloadAttemptState.Failed)
            return from != HostBridgeDownloadAttemptState.Cancelling;
        if (to == HostBridgeDownloadAttemptState.Cancelling)
            return from != HostBridgeDownloadAttemptState.Cancelling;

        return (from, to) switch
        {
            (HostBridgeDownloadAttemptState.Queued, HostBridgeDownloadAttemptState.Preparing) => true,
            (HostBridgeDownloadAttemptState.Preparing, HostBridgeDownloadAttemptState.Downloading) => true,
            (HostBridgeDownloadAttemptState.Downloading, HostBridgeDownloadAttemptState.Paused) => true,
            (HostBridgeDownloadAttemptState.Paused, HostBridgeDownloadAttemptState.Downloading) => true,
            (HostBridgeDownloadAttemptState.Downloading, HostBridgeDownloadAttemptState.Finalizing) => true,
            (HostBridgeDownloadAttemptState.Finalizing, HostBridgeDownloadAttemptState.CompletedImportable) => true,
            (HostBridgeDownloadAttemptState.Cancelling, HostBridgeDownloadAttemptState.Cancelled) => true,
            _ => false,
        };
    }
}
