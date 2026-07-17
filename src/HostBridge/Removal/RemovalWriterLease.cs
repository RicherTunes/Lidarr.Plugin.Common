using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

public sealed class RemovalWriterLease : IAsyncDisposable
{
    public const string RelativeLockPath = ".lpc-state/removals.lock";

    private readonly FileStream _stream;
    private object? _journalOwner;

    private RemovalWriterLease(SafeOwnedRoot root, FileStream stream)
    {
        RootId = root.RootId;
        _stream = stream;
    }

    internal string RootId { get; }

    internal bool IsHeld => _stream.SafeFileHandle is { IsClosed: false, IsInvalid: false };

    internal bool TryBindJournal(SafeOwnedRoot root, object owner) =>
        IsHeld
        && string.Equals(root.RootId, RootId, StringComparison.Ordinal)
        && root.RevalidateForJournal() == RemovalJournalError.None
        && Interlocked.CompareExchange(ref _journalOwner, owner, null) is null;

    internal bool IsHeldBy(object owner) =>
        IsHeld && ReferenceEquals(Volatile.Read(ref _journalOwner), owner);

    internal void UnbindJournal(object owner) =>
        _ = Interlocked.CompareExchange(ref _journalOwner, null, owner);

    public static ValueTask<RemovalWriterLeaseAcquireResult> AcquireAsync(
        SafeOwnedRoot root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();
        var rootValidation = root.RevalidateForJournal();
        if (rootValidation != RemovalJournalError.None)
        {
            return ValueTask.FromResult(new RemovalWriterLeaseAcquireResult(
                false,
                "QUEUE_REMOVAL_JOURNAL_FAILURE",
                null));
        }

        try
        {
            var stateDirectory = Path.Combine(root.RootPath, ".lpc-state");
            if (SafeOwnedRoot.IsLink(stateDirectory))
            {
                return ValueTask.FromResult(new RemovalWriterLeaseAcquireResult(false, "QUEUE_REMOVAL_JOURNAL_FAILURE", null));
            }

            Directory.CreateDirectory(stateDirectory);
            var lockPath = Path.Combine(root.RootPath, RelativeLockPath.Replace('/', Path.DirectorySeparatorChar));
            if (SafeOwnedRoot.IsLink(lockPath))
            {
                return ValueTask.FromResult(new RemovalWriterLeaseAcquireResult(false, "QUEUE_REMOVAL_JOURNAL_FAILURE", null));
            }

            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return ValueTask.FromResult(new RemovalWriterLeaseAcquireResult(
                true,
                "QUEUE_REMOVAL_WRITER_ACQUIRED",
                new RemovalWriterLease(root, stream)));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(new RemovalWriterLeaseAcquireResult(false, "QUEUE_REMOVAL_SECOND_WRITER", null));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(new RemovalWriterLeaseAcquireResult(false, "QUEUE_REMOVAL_JOURNAL_FAILURE", null));
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    // Synchronous release seam for callers running under a lock (the durable removal coordinator).
    internal void Close() => _stream.Dispose();
}

public readonly record struct RemovalWriterLeaseAcquireResult(
    bool Acquired,
    string Code,
    RemovalWriterLease? Lease);
