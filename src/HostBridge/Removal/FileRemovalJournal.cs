using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

public sealed class FileRemovalJournal : IRemovalJournal
{
    public const string RelativeJournalDirectory = ".lpc-state/removals";

    private const int MaxRecordCount = 10_000;
    private const int MaxRecordBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "operationId", "queueKey", "source", "quarantine", "fileIdentity",
        "capability", "state", "completionKind", "journalRevision", "createdAtUtc", "stateChangedAtUtc",
    };

    private readonly SafeOwnedRoot _root;
    private readonly RemovalWriterLease _lease;
    private readonly IRemovalJournalDurabilityHooks? _hooks;
    private readonly string _directory;
    private readonly object _gate = new();
    private bool _disposed;

    private FileRemovalJournal(
        SafeOwnedRoot root,
        RemovalWriterLease lease,
        string directory,
        IRemovalJournalDurabilityHooks? hooks)
    {
        _root = root;
        _lease = lease;
        _directory = directory;
        _hooks = hooks;
    }

    public static string RecordFileName(RemovalOperationId operationId) => $"{operationId}.json";

    public static string TempFileName(RemovalOperationId operationId, int processId, string randomHex32) =>
        $"{operationId}.json.tmp.{processId}.{randomHex32}";

    public static ValueTask<RemovalJournalResult<FileRemovalJournal>> OpenAsync(
        SafeOwnedRoot root,
        RemovalWriterLease lease,
        CancellationToken cancellationToken = default) =>
        OpenAsync(root, lease, hooks: null, cancellationToken);

    internal static ValueTask<RemovalJournalResult<FileRemovalJournal>> OpenAsync(
        SafeOwnedRoot root,
        RemovalWriterLease lease,
        IRemovalJournalDurabilityHooks? hooks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.IsHeld || !string.Equals(root.RootId, lease.RootId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(Failure<FileRemovalJournal>(RemovalJournalError.SecondWriter));
        }

        try
        {
            var stateDirectory = Path.Combine(root.RootPath, ".lpc-state");
            var directory = Path.Combine(root.RootPath, RelativeJournalDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (SafeOwnedRoot.IsLink(stateDirectory) || SafeOwnedRoot.IsLink(directory))
            {
                return ValueTask.FromResult(Failure<FileRemovalJournal>(RemovalJournalError.Corrupt));
            }

            Directory.CreateDirectory(directory);
            if (SafeOwnedRoot.IsLink(directory))
            {
                return ValueTask.FromResult(Failure<FileRemovalJournal>(RemovalJournalError.Corrupt));
            }

            return ValueTask.FromResult(Success(new FileRemovalJournal(root, lease, directory, hooks)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Failure<FileRemovalJournal>(RemovalJournalError.DurabilityFailure));
        }
    }

    public ValueTask<RemovalJournalResult<RemovalJournalEntry>> CreateAsync(
        RemovalJournalRecord prepared,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return CreateCore(prepared, cancellationToken);
        }
    }

    private ValueTask<RemovalJournalResult<RemovalJournalEntry>> CreateCore(
        RemovalJournalRecord prepared,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_lease.IsHeld)
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.SecondWriter));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidPrepared(prepared))
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.Corrupt));
        }

        var path = RecordPath(prepared.OperationId);
        if (File.Exists(path) || SafeOwnedRoot.IsLink(path))
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.Conflict));
        }

        if (Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly).Take(MaxRecordCount + 1).Count() >= MaxRecordCount)
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.BoundsExceeded));
        }

        var bytes = Serialize(prepared);
        if (bytes.Length > MaxRecordBytes)
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.BoundsExceeded));
        }

        var write = WriteAtomically(prepared.OperationId, path, bytes, createNew: true, cancellationToken);
        return ValueTask.FromResult(write == RemovalJournalError.None
            ? Success(ToEntry(prepared, bytes))
            : Failure<RemovalJournalEntry>(write));
    }

    public ValueTask<RemovalJournalResult<RemovalJournalEntry>> ReadAsync(
        RemovalOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lease.IsHeld)
                return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.SecondWriter));
            return ValueTask.FromResult(ReadCore(operationId));
        }
    }

    public ValueTask<RemovalJournalScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ScanCore(cancellationToken);
        }
    }

    private ValueTask<RemovalJournalScanResult> ScanCore(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_lease.IsHeld)
            return ValueTask.FromResult(ScanFailure(RemovalJournalError.SecondWriter));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var paths = Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            if (paths.Length > MaxRecordCount)
            {
                return ValueTask.FromResult(ScanFailure(RemovalJournalError.BoundsExceeded));
            }

            if (paths.Any(path => !IsCanonicalRecordFileName(Path.GetFileName(path))))
            {
                return ValueTask.FromResult(ScanFailure(RemovalJournalError.Corrupt));
            }

            var entries = new List<RemovalJournalEntry>(paths.Length);
            var operationIds = new HashSet<RemovalOperationId>();
            foreach (var path in paths.OrderBy(static path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idText = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(idText, "N", out var guid))
                {
                    return ValueTask.FromResult(ScanFailure(RemovalJournalError.Corrupt));
                }

                var operationId = new RemovalOperationId(guid);
                var result = ReadCore(operationId);
                if (!result.Succeeded)
                {
                    return ValueTask.FromResult(ScanFailure(result.Error));
                }

                if (!operationIds.Add(result.Value.Record.OperationId))
                {
                    return ValueTask.FromResult(ScanFailure(RemovalJournalError.Corrupt));
                }

                entries.Add(result.Value);
            }

            return ValueTask.FromResult(new RemovalJournalScanResult(true, RemovalJournalError.None, entries));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(ScanFailure(RemovalJournalError.DurabilityFailure));
        }
    }

    public ValueTask<RemovalJournalResult<RemovalJournalEntry>> CompareExchangeAsync(
        RemovalJournalEntry expected,
        RemovalJournalRecord updated,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return CompareExchangeCore(expected, updated, cancellationToken);
        }
    }

    private ValueTask<RemovalJournalResult<RemovalJournalEntry>> CompareExchangeCore(
        RemovalJournalEntry expected,
        RemovalJournalRecord updated,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_lease.IsHeld)
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.SecondWriter));
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadCore(expected.Record.OperationId);
        if (!current.Succeeded)
        {
            return ValueTask.FromResult(current);
        }

        if (current.Value.Record != expected.Record || current.Value.CasToken != expected.CasToken)
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.Conflict));
        }

        if (!IsValidUpdate(expected.Record, updated))
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.InvalidTransition));
        }

        var bytes = Serialize(updated);
        if (bytes.Length > MaxRecordBytes)
        {
            return ValueTask.FromResult(Failure<RemovalJournalEntry>(RemovalJournalError.BoundsExceeded));
        }

        var write = WriteAtomically(updated.OperationId, RecordPath(updated.OperationId), bytes, createNew: false, cancellationToken);
        return ValueTask.FromResult(write == RemovalJournalError.None
            ? Success(ToEntry(updated, bytes))
            : Failure<RemovalJournalEntry>(write));
    }

    public ValueTask<RemovalJournalResult<bool>> CompactAsync(
        RemovalJournalEntry expectedCompleted,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return CompactCore(expectedCompleted, cancellationToken);
        }
    }

    private ValueTask<RemovalJournalResult<bool>> CompactCore(
        RemovalJournalEntry expectedCompleted,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_lease.IsHeld)
            return ValueTask.FromResult(Failure<bool>(RemovalJournalError.SecondWriter));
        cancellationToken.ThrowIfCancellationRequested();
        var completionValid =
            expectedCompleted.Record is { State: RemovalJournalState.Deleted, CompletionKind: RemovalCompletionKind.AutomaticDeletion }
            || expectedCompleted.Record is { State: RemovalJournalState.Quarantined, CompletionKind: RemovalCompletionKind.ManualAcknowledgement };
        if (!completionValid)
        {
            return ValueTask.FromResult(Failure<bool>(RemovalJournalError.InvalidTransition));
        }

        var current = ReadCore(expectedCompleted.Record.OperationId);
        if (!current.Succeeded)
        {
            return ValueTask.FromResult(Failure<bool>(current.Error));
        }

        if (current.Value != expectedCompleted)
        {
            return ValueTask.FromResult(Failure<bool>(RemovalJournalError.Conflict));
        }

        var path = RecordPath(expectedCompleted.Record.OperationId);
        byte[]? durableBytes = null;
        try
        {
            durableBytes = File.ReadAllBytes(path);
            _hooks?.BeforeCompactionUnlink(path);
            File.Delete(path);
            _hooks?.BeforeCompactionParentFlush(_directory);
            FlushDirectoryIfSupported(_directory);
            return ValueTask.FromResult(Success(true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (durableBytes is not null && !File.Exists(path))
            {
                _ = WriteAtomically(
                    expectedCompleted.Record.OperationId,
                    path,
                    durableBytes,
                    createNew: true,
                    CancellationToken.None);
            }

            return ValueTask.FromResult(Failure<bool>(RemovalJournalError.DurabilityFailure));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private RemovalJournalResult<RemovalJournalEntry> ReadCore(RemovalOperationId operationId)
    {
        var path = RecordPath(operationId);
        if (SafeOwnedRoot.IsLink(path))
        {
            return Failure<RemovalJournalEntry>(RemovalJournalError.Corrupt);
        }

        if (!File.Exists(path))
        {
            return Failure<RemovalJournalEntry>(RemovalJournalError.NotFound);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxRecordBytes)
            {
                return Failure<RemovalJournalEntry>(RemovalJournalError.BoundsExceeded);
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > MaxRecordBytes || !HasStrictShape(bytes))
            {
                return Failure<RemovalJournalEntry>(bytes.Length > MaxRecordBytes
                    ? RemovalJournalError.BoundsExceeded
                    : RemovalJournalError.Corrupt);
            }

            var record = JsonSerializer.Deserialize<RemovalJournalRecord>(bytes, JsonOptions);
            if (record is null || !IsValidPersisted(record) || record.OperationId != operationId)
            {
                return Failure<RemovalJournalEntry>(RemovalJournalError.Corrupt);
            }

            return Success(ToEntry(record, bytes));
        }
        catch (JsonException)
        {
            return Failure<RemovalJournalEntry>(RemovalJournalError.Corrupt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure<RemovalJournalEntry>(RemovalJournalError.DurabilityFailure);
        }
    }

    private RemovalJournalError WriteAtomically(
        RemovalOperationId operationId,
        string destination,
        byte[] bytes,
        bool createNew,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(
            _directory,
            TempFileName(operationId, Environment.ProcessId, Guid.NewGuid().ToString("N")));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                _hooks?.BeforeTempFlush(temp);
                stream.Flush(flushToDisk: true);
                _hooks?.AfterTempFlush(temp);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _hooks?.BeforeAtomicReplace(temp, destination);
            if (createNew)
            {
                File.Move(temp, destination, overwrite: false);
            }
            else
            {
                File.Move(temp, destination, overwrite: true);
            }

            _hooks?.AfterAtomicReplace(destination);
            _hooks?.BeforeParentFlush(_directory);
            FlushDirectoryIfSupported(_directory);
            return RemovalJournalError.None;
        }
        catch (IOException)
        {
            return createNew && File.Exists(destination)
                ? RemovalJournalError.Conflict
                : RemovalJournalError.DurabilityFailure;
        }
        catch (UnauthorizedAccessException)
        {
            return RemovalJournalError.DurabilityFailure;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A uniquely named abandoned temp is ignored by no operation: scans fail closed.
            }
        }
    }

    private bool IsValidPrepared(RemovalJournalRecord record) =>
        record.SchemaVersion == 1
        && record.State == RemovalJournalState.Prepared
        && record.CompletionKind is null
        && record.JournalRevision == 1
        && IsValidPersisted(record)
        && record.QuarantineRelativePath.Value == CanonicalQuarantine(record.OperationId);

    private bool IsValidPersisted(RemovalJournalRecord record)
    {
        if (record.SchemaVersion != 1
            || record.OperationId.Value == Guid.Empty
            || record.JournalRevision < 1
            || string.IsNullOrEmpty(record.SourceRelativePath.Value)
            || string.IsNullOrEmpty(record.QuarantineRelativePath.Value)
            || record.QuarantineRelativePath.Value != CanonicalQuarantine(record.OperationId)
            || record.Identity is null
            || record.Identity.Version != 1
            || record.Identity.MarkerHex is not { Length: 64 } markerHex
            || markerHex.Any(static character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            || string.IsNullOrEmpty(record.Identity.RootId)
            || !string.Equals(record.Identity.RootId, _root.RootId, StringComparison.Ordinal)
            || !Enum.IsDefined(record.Identity.EntryType)
            || !Enum.IsDefined(record.Capability)
            || !Enum.IsDefined(record.State)
            || (record.CompletionKind is not null && !Enum.IsDefined(record.CompletionKind.Value))
            || record.CreatedAtUtc.Kind != DateTimeKind.Utc
            || record.StateChangedAtUtc.Kind != DateTimeKind.Utc
            || record.StateChangedAtUtc < record.CreatedAtUtc)
        {
            return false;
        }

        return record.State switch
        {
            RemovalJournalState.Prepared or RemovalJournalState.Quarantined or RemovalJournalState.Deleting =>
                record.CompletionKind is null
                || record is { State: RemovalJournalState.Quarantined, CompletionKind: RemovalCompletionKind.ManualAcknowledgement },
            RemovalJournalState.Deleted => record.CompletionKind == RemovalCompletionKind.AutomaticDeletion,
            _ => false,
        };
    }

    private bool IsValidUpdate(RemovalJournalRecord previous, RemovalJournalRecord updated)
    {
        if (updated.SchemaVersion != previous.SchemaVersion
            || updated.OperationId != previous.OperationId
            || updated.QueueKey != previous.QueueKey
            || updated.SourceRelativePath != previous.SourceRelativePath
            || updated.QuarantineRelativePath != previous.QuarantineRelativePath
            || updated.Identity != previous.Identity
            || updated.Capability != previous.Capability
            || updated.CreatedAtUtc != previous.CreatedAtUtc
            || updated.JournalRevision != previous.JournalRevision + 1
            || updated.StateChangedAtUtc <= previous.StateChangedAtUtc
            || !IsValidPersisted(updated))
        {
            return false;
        }

        return (previous.State, previous.CompletionKind, updated.State, updated.CompletionKind) switch
        {
            (RemovalJournalState.Prepared, null, RemovalJournalState.Quarantined, null) => true,
            (RemovalJournalState.Quarantined, null, RemovalJournalState.Deleting, null) => true,
            (RemovalJournalState.Deleting, null, RemovalJournalState.Deleted, RemovalCompletionKind.AutomaticDeletion) => true,
            (RemovalJournalState.Quarantined, null, RemovalJournalState.Quarantined, RemovalCompletionKind.ManualAcknowledgement) => true,
            _ => false,
        };
    }

    private static bool HasStrictShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasUniqueProperties(document.RootElement)
            && document.RootElement.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(RequiredProperties);
    }

    private static bool HasUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!HasUniqueProperties(item))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static byte[] Serialize(RemovalJournalRecord record) => JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

    private static RemovalJournalEntry ToEntry(RemovalJournalRecord record, byte[] bytes) =>
        new(record, new RemovalJournalCasToken(
            record.JournalRevision,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));

    private string RecordPath(RemovalOperationId operationId) => Path.Combine(_directory, RecordFileName(operationId));

    private static string CanonicalQuarantine(RemovalOperationId operationId) =>
        string.Join(Path.DirectorySeparatorChar, ".lpc-trash", operationId.ToString());

    private static bool IsCanonicalRecordFileName(string name)
    {
        if (name.Length != 37 || !name.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParseExact(name.AsSpan(0, 32), "N", out _)
            && name.AsSpan(0, 32).IndexOfAnyInRange('A', 'F') < 0;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new RemovalOperationIdConverter());
        options.Converters.Add(new RelativeStagingPathConverter());
        return options;
    }

    private static void FlushDirectoryIfSupported(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var handle = File.OpenHandle(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception error) when (error is NotSupportedException or PlatformNotSupportedException)
        {
            // Some managed runtimes/filesystems cannot open directory handles. The replace remains durable.
        }
    }

    private static RemovalJournalResult<T> Success<T>(T value) => new(true, RemovalJournalError.None, value);

    private static RemovalJournalResult<T> Failure<T>(RemovalJournalError error) => new(false, error, default);

    private static RemovalJournalScanResult ScanFailure(RemovalJournalError error) =>
        new(false, error, Array.Empty<RemovalJournalEntry>());

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class RemovalOperationIdConverter : JsonConverter<RemovalOperationId>
    {
        public override RemovalOperationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (text is null || text.Length != 32 || !Guid.TryParseExact(text, "N", out var value))
            {
                throw new JsonException("Invalid removal operation ID.");
            }

            return new RemovalOperationId(value);
        }

        public override void Write(Utf8JsonWriter writer, RemovalOperationId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class RelativeStagingPathConverter : JsonConverter<RelativeStagingPath>
    {
        public override RelativeStagingPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                return RelativeStagingPath.Create(reader.GetString() ?? string.Empty);
            }
            catch (ArgumentException error)
            {
                throw new JsonException("Invalid relative staging path.", error);
            }
        }

        public override void Write(Utf8JsonWriter writer, RelativeStagingPath value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
