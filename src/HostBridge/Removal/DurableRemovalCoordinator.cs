using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Coordinator-level crash-fault seams (5B). Distinct from
/// <see cref="IRemovalJournalDurabilityHooks"/> (which injects faults into journal writes): these
/// inject a hard-crash simulation into the coordinator's filesystem phases. A hook that throws is
/// NOT caught by the coordinator — it propagates like a killed process, leaving the WAL + staging
/// tree in a mid-flight state that <see cref="DurableRemovalCoordinator.RecoverAsync"/> must
/// resolve without orphaning or double-deleting files.
/// </summary>
internal interface IDurableRemovalFaultHooks
{
    void BeforeQuarantineMove();
    void AfterQuarantineMove();
    void BeforeMappingRemoval();
    void BeforePhysicalDelete();
    void AfterPhysicalDelete();
}

/// <summary>
/// Two-phase, crash-recoverable removal of a single terminal AttemptV2 download's owned staging
/// tree, coordinating <see cref="SafeOwnedRoot"/> (containment/link safety),
/// <see cref="RemovalWriterLease"/> (single writer), and <see cref="FileRemovalJournal"/> (WAL).
///
/// <para><strong>Happy path</strong>: journal Prepared → atomic move of the owned tree into
/// <c>.lpc-trash/&lt;operationId&gt;</c> (the point of no return that frees the original
/// OutputPath) → CAS Quarantined → remove the queue mapping → CAS Deleting → recursive delete of
/// the quarantined tree → CAS Deleted → compact.</para>
///
/// <para><strong>Crash recovery</strong> (idempotent, re-runnable) keys purely on whether the
/// quarantine directory still exists, so it never re-initiates a move of a live OutputPath that a
/// restart may have re-grabbed:</para>
/// <list type="bullet">
///   <item>Deleted → compact.</item>
///   <item>quarantine present → resume: advance to Deleting, delete the tree, CAS Deleted,
///         compact (orphaned quarantine has no owner, so it MUST be removed).</item>
///   <item>quarantine absent → the move never ran (or the tree is already gone): clear the WAL via
///         the ManualAcknowledgement terminal edge without deleting any live source.</item>
/// </list>
///
/// <para><strong>Locking</strong>: <see cref="Execute"/> runs synchronously under the store's
/// membership + mutation locks (the journal completes synchronously) so the re-grab guard stays
/// atomic through the quarantine move. The recursive delete of the coordinator-private quarantine
/// tree is the only large operation and is still on-lock in this revision; moving it off-lock is
/// deferred (5B item 5) because a safe off-lock protocol needs a path reservation to preserve the
/// re-grab guarantee.</para>
/// </summary>
internal static class DurableRemovalCoordinator
{
    private const string TrashDirectoryName = ".lpc-trash";
    private const int MaxSubtreeEntries = 200_000;

    internal readonly record struct Outcome(
        string Code,
        bool StateRemoved,
        bool FilesRemoved,
        bool SafeOrphanRetained,
        RemovalOperationId? OperationId,
        RemovalJournalState? State,
        QueueRemovalDurability Durability,
        string? Warning);

    /// <summary>
    /// Executes the durable removal for a terminal attempt. Returns an outcome the tracker maps
    /// onto its public removal result. <paramref name="anotherActiveOwnerExists"/> and
    /// <paramref name="removeMappingAndPersist"/> are invoked while the caller still holds the
    /// membership + mutation locks.
    /// </summary>
    internal static Outcome Execute(
        string ownedStagingRoot,
        HostBridgeQueueMutationKey key,
        string outputPath,
        Func<bool> anotherActiveOwnerExists,
        Func<string?> removeMappingAndPersist,
        Func<DateTime> utcNow,
        IDurableRemovalFaultHooks? faultHooks,
        IRemovalJournalDurabilityHooks? journalHooks)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            // Nothing to delete but the caller asked for state removal; drop the mapping.
            var warning = removeMappingAndPersist();
            return new Outcome(
                HostBridgeQueueResultCodes.Removed, true, true, false, null, null,
                QueueRemovalDurability.Volatile, warning);
        }

        var boundary = ComputeBoundary(ownedStagingRoot);
        var openResult = Sync(SafeOwnedRoot.OpenAsync(ownedStagingRoot, boundary));
        if (!openResult.Opened)
        {
            // Root became a link / drifted / fell outside its boundary after the attempt was
            // queued (covers root reparse points and removal-time root identity revalidation).
            var code = openResult.Code == "QUEUE_REMOVAL_ROOT_OUTSIDE_BOUNDARY"
                ? HostBridgeQueueResultCodes.SafeOrphanOutsideRoot
                : HostBridgeQueueResultCodes.SafeOrphanLinkTraversal;
            return Retained(code);
        }

        var root = openResult.Root!;
        try
        {
            var canonicalOutput = Canonicalize(outputPath);

            // Containment: the OutputPath must be a strict descendant of the owned root. The root
            // itself, siblings, and ../escapes are refused and retained.
            if (PathEquals(canonicalOutput, root.RootPath) || !root.OwnsPath(outputPath))
                return Retained(HostBridgeQueueResultCodes.SafeOrphanOutsideRoot);

            // Link safety: the OutputPath, any ancestor up to the root, or any entry inside the
            // subtree being a reparse point means a recursive delete could escape the owned root.
            if (AnyLinkFromOutputUpToRoot(canonicalOutput, root.RootPath)
                || ContainsReparsePoint(canonicalOutput))
            {
                return Retained(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal);
            }

            // Cross-attempt re-grab guard: another active download owns the same canonical path, so
            // it now owns the directory lifecycle — remove only the terminal mapping, keep files.
            if (anotherActiveOwnerExists())
            {
                var warning = removeMappingAndPersist();
                return new Outcome(
                    HostBridgeQueueResultCodes.Removed, true, false, false, null, null,
                    QueueRemovalDurability.Volatile, warning);
            }

            // Missing in-root source: the delete is a successful no-op.
            if (!Directory.Exists(canonicalOutput))
            {
                var warning = removeMappingAndPersist();
                return new Outcome(
                    HostBridgeQueueResultCodes.Removed, true, true, false, null, null,
                    QueueRemovalDurability.Volatile, warning);
            }

            return ExecuteTwoPhase(
                root, key, canonicalOutput, removeMappingAndPersist, utcNow, faultHooks, journalHooks);
        }
        finally
        {
            Sync(root.DisposeAsync());
        }
    }

    private static Outcome ExecuteTwoPhase(
        SafeOwnedRoot root,
        HostBridgeQueueMutationKey key,
        string canonicalOutput,
        Func<string?> removeMappingAndPersist,
        Func<DateTime> utcNow,
        IDurableRemovalFaultHooks? faultHooks,
        IRemovalJournalDurabilityHooks? journalHooks)
    {
        var acquired = Sync(RemovalWriterLease.AcquireAsync(root));
        if (!acquired.Acquired)
            return Failure(HostBridgeQueueResultCodes.RemovalSecondWriter);

        var lease = acquired.Lease!;
        try
        {
            var openJournal = Sync(FileRemovalJournal.OpenAsync(root, lease, journalHooks));
            if (!openJournal.Succeeded)
                return Failure(HostBridgeQueueResultCodes.RemovalJournalFailure);

            var journal = openJournal.Value!;
            try
            {
                // Finish any interrupted prior operation before starting a new one.
                var recovered = RecoverCore(root, journal, utcNow);
                if (recovered != RemovalJournalError.None)
                    return Failure(HostBridgeQueueResultCodes.QueueDurabilityFailure);

                var operationId = RemovalOperationId.New();
                var quarantinePath = Path.Combine(root.RootPath, TrashDirectoryName, operationId.ToString());
                var createdAt = EnsureUtc(utcNow());
                var relativeSource = RelativeStagingPath.Create(
                    Path.GetRelativePath(root.RootPath, canonicalOutput));
                var relativeQuarantine = RelativeStagingPath.Create(TrashDirectoryName + "/" + operationId);

                var prepared = new RemovalJournalRecord(
                    SchemaVersion: 1,
                    OperationId: operationId,
                    QueueKey: key,
                    SourceRelativePath: relativeSource,
                    QuarantineRelativePath: relativeQuarantine,
                    Identity: root.RootMarkerIdentity,
                    Capability: root.Capability,
                    State: RemovalJournalState.Prepared,
                    CompletionKind: null,
                    JournalRevision: 1,
                    CreatedAtUtc: createdAt,
                    StateChangedAtUtc: createdAt);

                var createdEntry = Sync(journal.CreateAsync(prepared));
                if (!createdEntry.Succeeded)
                    return Failure(HostBridgeQueueResultCodes.RemovalJournalFailure);

                // Point of no return: atomically move the owned tree into quarantine, freeing the
                // OutputPath. A crash before this leaves a Prepared record with no quarantine, which
                // recovery abandons via ManualAcknowledgement (source untouched).
                var trashParent = Path.Combine(root.RootPath, TrashDirectoryName);
                if (SafeOwnedRoot.IsLink(trashParent))
                    return Failure(HostBridgeQueueResultCodes.SafeOrphanLinkTraversal);
                Directory.CreateDirectory(trashParent);

                faultHooks?.BeforeQuarantineMove();
                Directory.Move(canonicalOutput, quarantinePath);
                faultHooks?.AfterQuarantineMove();

                var quarantined = CasNext(
                    journal, createdEntry.Value, RemovalJournalState.Quarantined, null, utcNow);
                if (!quarantined.Succeeded)
                    return Failure(HostBridgeQueueResultCodes.QueueDurabilityFailure);

                // The original path is now free; the queue mapping is safe to remove.
                faultHooks?.BeforeMappingRemoval();
                var warning = removeMappingAndPersist();

                var deleting = CasNext(
                    journal, quarantined.Value, RemovalJournalState.Deleting, null, utcNow);
                if (!deleting.Succeeded)
                    return Failure(HostBridgeQueueResultCodes.QueueDurabilityFailure);

                faultHooks?.BeforePhysicalDelete();
                DeleteQuarantinedTree(quarantinePath);
                faultHooks?.AfterPhysicalDelete();

                var deleted = CasNext(
                    journal, deleting.Value, RemovalJournalState.Deleted,
                    RemovalCompletionKind.AutomaticDeletion, utcNow);
                if (!deleted.Succeeded)
                    return Failure(HostBridgeQueueResultCodes.QueueDurabilityFailure);

                _ = Sync(journal.PurgeAsync(deleted.Value));

                return new Outcome(
                    HostBridgeQueueResultCodes.Removed, true, true, false, operationId,
                    RemovalJournalState.Deleted, QueueRemovalDurability.Durable, warning);
            }
            finally
            {
                Sync(journal.DisposeAsync());
            }
        }
        finally
        {
            Sync(lease.DisposeAsync());
        }
    }

    /// <summary>
    /// Opens the owned root + journal and replays/abandons every in-flight WAL record. Safe to
    /// call at startup and safe to re-run after a crash mid-recovery.
    /// </summary>
    internal static async Task<bool> RecoverAsync(
        string ownedStagingRoot,
        Func<DateTime> utcNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownedStagingRoot))
            return true;

        cancellationToken.ThrowIfCancellationRequested();
        var boundary = ComputeBoundary(ownedStagingRoot);
        var openResult = await SafeOwnedRoot.OpenAsync(ownedStagingRoot, boundary, cancellationToken)
            .ConfigureAwait(false);
        if (!openResult.Opened)
            return false;

        var root = openResult.Root!;
        try
        {
            var acquired = await RemovalWriterLease.AcquireAsync(root, cancellationToken).ConfigureAwait(false);
            if (!acquired.Acquired)
                return false;

            var lease = acquired.Lease!;
            try
            {
                var openJournal = await FileRemovalJournal.OpenAsync(root, lease, cancellationToken)
                    .ConfigureAwait(false);
                if (!openJournal.Succeeded)
                    return false;

                var journal = openJournal.Value!;
                try
                {
                    return RecoverCore(root, journal, utcNow) == RemovalJournalError.None;
                }
                finally
                {
                    await journal.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await root.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static RemovalJournalError RecoverCore(
        SafeOwnedRoot root,
        FileRemovalJournal journal,
        Func<DateTime> utcNow)
    {
        var scan = Sync(journal.ScanAsync());
        if (!scan.Succeeded)
            return scan.Error;

        foreach (var entry in scan.Entries)
        {
            var error = RecoverEntry(root, journal, entry, utcNow);
            if (error != RemovalJournalError.None)
                return error;
        }

        return RemovalJournalError.None;
    }

    private static RemovalJournalError RecoverEntry(
        SafeOwnedRoot root,
        FileRemovalJournal journal,
        RemovalJournalEntry entry,
        Func<DateTime> utcNow)
    {
        var current = entry;

        // Already-completed records (a crash between the terminal CAS and the purge, or a re-run of
        // recovery over compacted tombstones) are simply purged. Keeps recovery idempotent.
        if (current.Record.CompletionKind is not null)
            return Purge(journal, current);

        var quarantinePath = Path.Combine(
            root.RootPath, current.Record.QuarantineRelativePath.ToNativeRelativePath());
        var quarantineExists = Directory.Exists(quarantinePath) || SafeOwnedRoot.IsLink(quarantinePath);

        if (quarantineExists)
        {
            // Resume: the tree is orphaned in quarantine and must be deleted.
            if (current.Record.State == RemovalJournalState.Prepared)
            {
                var quarantined = CasNext(journal, current, RemovalJournalState.Quarantined, null, utcNow);
                if (!quarantined.Succeeded)
                    return quarantined.Error;
                current = quarantined.Value;
            }

            if (current.Record.State == RemovalJournalState.Quarantined)
            {
                var deleting = CasNext(journal, current, RemovalJournalState.Deleting, null, utcNow);
                if (!deleting.Succeeded)
                    return deleting.Error;
                current = deleting.Value;
            }

            try
            {
                DeleteQuarantinedTree(quarantinePath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return RemovalJournalError.DurabilityFailure;
            }

            var deleted = CasNext(
                journal, current, RemovalJournalState.Deleted,
                RemovalCompletionKind.AutomaticDeletion, utcNow);
            if (!deleted.Succeeded)
                return deleted.Error;
            return Purge(journal, deleted.Value);
        }

        // Quarantine absent: the move never ran (or the tree is already gone). Never delete a live
        // source that a restart may have re-grabbed — clear the WAL via ManualAcknowledgement.
        if (current.Record.State == RemovalJournalState.Deleting)
        {
            // The physical delete already happened (quarantine gone) but the completion CAS did
            // not — finish it as an automatic deletion.
            var deleted = CasNext(
                journal, current, RemovalJournalState.Deleted,
                RemovalCompletionKind.AutomaticDeletion, utcNow);
            if (!deleted.Succeeded)
                return deleted.Error;
            return Purge(journal, deleted.Value);
        }

        if (current.Record.State == RemovalJournalState.Prepared)
        {
            var quarantined = CasNext(journal, current, RemovalJournalState.Quarantined, null, utcNow);
            if (!quarantined.Succeeded)
                return quarantined.Error;
            current = quarantined.Value;
        }

        var acknowledged = CasNext(
            journal, current, RemovalJournalState.Quarantined,
            RemovalCompletionKind.ManualAcknowledgement, utcNow);
        if (!acknowledged.Succeeded)
            return acknowledged.Error;
        return Purge(journal, acknowledged.Value);
    }

    private static RemovalJournalError Purge(FileRemovalJournal journal, RemovalJournalEntry completed)
    {
        var result = Sync(journal.PurgeAsync(completed));
        return result.Succeeded ? RemovalJournalError.None : result.Error;
    }

    private static RemovalJournalResult<RemovalJournalEntry> CasNext(
        FileRemovalJournal journal,
        RemovalJournalEntry current,
        RemovalJournalState state,
        RemovalCompletionKind? completion,
        Func<DateTime> utcNow)
    {
        var changedAt = EnsureUtc(utcNow());
        if (changedAt <= current.Record.StateChangedAtUtc)
            changedAt = current.Record.StateChangedAtUtc.AddTicks(1);

        var updated = current.Record with
        {
            State = state,
            CompletionKind = completion,
            JournalRevision = current.Record.JournalRevision + 1,
            StateChangedAtUtc = changedAt,
        };
        return Sync(journal.CompareExchangeAsync(current, updated));
    }

    private static void DeleteQuarantinedTree(string quarantinePath)
    {
        if (Directory.Exists(quarantinePath))
            Directory.Delete(quarantinePath, recursive: true);
    }

    private static bool AnyLinkFromOutputUpToRoot(string canonicalOutput, string rootPath)
    {
        for (var current = new DirectoryInfo(canonicalOutput);
             current is not null && !PathEquals(current.FullName, rootPath);
             current = current.Parent)
        {
            if (SafeOwnedRoot.IsLink(current.FullName))
                return true;
        }

        return false;
    }

    private static bool ContainsReparsePoint(string root)
    {
        if (!Directory.Exists(root))
            return false;

        try
        {
            var seen = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (++seen > MaxSubtreeEntries)
                    return true; // Pathologically large tree: refuse conservatively.
                if (SafeOwnedRoot.IsLink(entry))
                    return true;
            }

            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A traversal that trips on a link/permission mid-walk is treated as unsafe.
            return true;
        }
    }

    private static Outcome Retained(string code) =>
        new(code, false, false, true, null, null, QueueRemovalDurability.None, null);

    private static Outcome Failure(string code) =>
        new(code, false, false, false, null, null, QueueRemovalDurability.None, null);

    private static string ComputeBoundary(string ownedStagingRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedStagingRoot));
        return Path.GetDirectoryName(full) is { Length: > 0 } parent ? parent : full;
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static T Sync<T>(ValueTask<T> valueTask) =>
        valueTask.IsCompletedSuccessfully ? valueTask.Result : valueTask.AsTask().GetAwaiter().GetResult();

    private static void Sync(ValueTask valueTask)
    {
        if (valueTask.IsCompletedSuccessfully)
            return;
        valueTask.AsTask().GetAwaiter().GetResult();
    }
}
