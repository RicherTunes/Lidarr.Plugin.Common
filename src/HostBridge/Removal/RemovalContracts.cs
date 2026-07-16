using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

public readonly record struct RemovalOperationId(Guid Value)
{
    public static RemovalOperationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public enum RemovalJournalState { Prepared = 1, Quarantined = 2, Deleting = 3, Deleted = 4 }

public enum RemovalCompletionKind { AutomaticDeletion = 1, ManualAcknowledgement = 2 }

public enum StagingDeletionCapability { QuarantineOnly = 0, ProtectedRootCleanup = 1 }

public enum FileIdentityEntryType { File = 1, Directory = 2 }

public sealed record FileIdentity(
    int Version,
    string MarkerHex,
    string RootId,
    FileIdentityEntryType EntryType);

public sealed record RemovalJournalRecord(
    [property: JsonPropertyName("schemaVersion"), JsonRequired] int SchemaVersion,
    [property: JsonPropertyName("operationId"), JsonRequired] RemovalOperationId OperationId,
    [property: JsonPropertyName("queueKey"), JsonRequired] HostBridgeQueueMutationKey QueueKey,
    [property: JsonPropertyName("source"), JsonRequired] RelativeStagingPath SourceRelativePath,
    [property: JsonPropertyName("quarantine"), JsonRequired] RelativeStagingPath QuarantineRelativePath,
    [property: JsonPropertyName("fileIdentity"), JsonRequired] FileIdentity Identity,
    [property: JsonPropertyName("capability"), JsonRequired] StagingDeletionCapability Capability,
    [property: JsonPropertyName("state"), JsonRequired] RemovalJournalState State,
    [property: JsonPropertyName("completionKind"), JsonRequired] RemovalCompletionKind? CompletionKind,
    [property: JsonPropertyName("journalRevision"), JsonRequired] long JournalRevision,
    [property: JsonPropertyName("createdAtUtc"), JsonRequired] DateTime CreatedAtUtc,
    [property: JsonPropertyName("stateChangedAtUtc"), JsonRequired] DateTime StateChangedAtUtc);

public readonly record struct RemovalJournalCasToken(long JournalRevision, string CanonicalSha256);

public readonly record struct RemovalJournalEntry(RemovalJournalRecord Record, RemovalJournalCasToken CasToken);

public enum RemovalJournalError
{
    None = 0,
    NotFound = 1,
    Conflict = 2,
    Corrupt = 3,
    BoundsExceeded = 4,
    DurabilityFailure = 5,
    InvalidTransition = 6,
    SecondWriter = 7,
}

public readonly record struct RemovalJournalResult<T>(bool Succeeded, RemovalJournalError Error, T? Value);

public readonly record struct RemovalJournalScanResult(
    bool Succeeded,
    RemovalJournalError Error,
    IReadOnlyList<RemovalJournalEntry> Entries);

public interface IRemovalJournal : IAsyncDisposable
{
    ValueTask<RemovalJournalResult<RemovalJournalEntry>> CreateAsync(
        RemovalJournalRecord prepared,
        CancellationToken cancellationToken = default);

    ValueTask<RemovalJournalResult<RemovalJournalEntry>> ReadAsync(
        RemovalOperationId operationId,
        CancellationToken cancellationToken = default);

    ValueTask<RemovalJournalScanResult> ScanAsync(CancellationToken cancellationToken = default);

    ValueTask<RemovalJournalResult<RemovalJournalEntry>> CompareExchangeAsync(
        RemovalJournalEntry expected,
        RemovalJournalRecord updated,
        CancellationToken cancellationToken = default);

    ValueTask<RemovalJournalResult<bool>> CompactAsync(
        RemovalJournalEntry expectedCompleted,
        CancellationToken cancellationToken = default);
}

internal interface IRemovalJournalDurabilityHooks
{
    void BeforeTempFlush(string path);
    void AfterTempFlush(string path);
    void BeforeAtomicReplace(string temp, string destination);
    void AfterAtomicReplace(string destination);
    void BeforeParentFlush(string directory);
    void BeforeCompactionUnlink(string path);
    void BeforeCompactionParentFlush(string directory);
}
