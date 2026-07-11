using System;

namespace Lidarr.Plugin.Common.HostBridge;

public enum HostBridgeQueueContractVersion { LegacyV1 = 1, AttemptV2 = 2 }

public enum HostBridgeDownloadAttemptState
{
    Queued = 0,
    Preparing = 1,
    Downloading = 2,
    Paused = 3,
    Finalizing = 4,
    CompletedImportable = 5,
    Failed = 6,
    Cancelling = 7,
    Cancelled = 8,
}

public readonly record struct HostBridgeQueueMutationKey(
    string DownloadId,
    Guid AttemptId,
    long Revision);

public readonly record struct HostBridgePreAdmissionResult(bool Accepted, string Code, string Message)
{
    public static HostBridgePreAdmissionResult Allow() => new(true, "ADMISSION_ACCEPTED", string.Empty);
    public static HostBridgePreAdmissionResult Reject(string code, string message) => new(false, code, message);
}

public sealed class HostBridgePreAdmissionException : InvalidOperationException
{
    public HostBridgePreAdmissionException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public readonly record struct HostBridgeRestartEvidence(
    bool AttemptTemporaryStateContained,
    bool VerifiedSegmentsAvailable,
    bool FinalizedOutputValid);

public readonly record struct HostBridgeQueueMutationResult<TItem>(
    bool Applied,
    string Code,
    HostBridgeQueueMutationKey Current,
    TItem? Item)
    where TItem : HostBridgeDownloadItem;

public readonly record struct HostBridgeQueueRemovalResult<TItem>(
    bool Found,
    bool StateRemoved,
    bool FilesRemoved,
    bool SafeOrphanRetained,
    string Code,
    TItem? Item)
    where TItem : HostBridgeDownloadItem;

public sealed class HostBridgeQueueStoreOptions
{
    public HostBridgeQueueContractVersion ContractVersion { get; init; } = HostBridgeQueueContractVersion.LegacyV1;
    public string? OwnedStagingRoot { get; init; }
    public Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;
    public Func<HostBridgeDownloadItemDto, HostBridgeRestartEvidence> RestartEvidence { get; init; } =
        static _ => new(false, false, false);
}

public static class HostBridgeQueueResultCodes
{
    public const string Applied = "QUEUE_APPLIED";
    public const string Conflict = "QUEUE_CONFLICT";
    public const string IllegalTransition = "QUEUE_ILLEGAL_TRANSITION";
    public const string MetadataExhausted = "QUEUE_METADATA_EXHAUSTED";
    public const string PersistenceLimitExceeded = "QUEUE_PERSISTENCE_LIMIT_EXCEEDED";
    public const string PersistenceInvalidState = "QUEUE_PERSISTENCE_INVALID_STATE";
    public const string NotFound = "QUEUE_NOT_FOUND";
    public const string WorkerShutdownTimeout = "QUEUE_WORKER_SHUTDOWN_TIMEOUT";
    public const string Removed = "QUEUE_REMOVED";
    public const string SafeOrphanOutsideRoot = "QUEUE_SAFE_ORPHAN_OUTSIDE_ROOT";
    public const string SafeOrphanLinkTraversal = "QUEUE_SAFE_ORPHAN_LINK_TRAVERSAL";
    public const string SafeOrphanDeleteFailed = "QUEUE_SAFE_ORPHAN_DELETE_FAILED";
}
