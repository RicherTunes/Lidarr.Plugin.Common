using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class RemovalJournalDurabilityTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(
        Path.GetTempPath(), "removal-journal-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> LegalTransitions()
    {
        yield return new object[]
        {
            RemovalJournalState.Prepared,
            null!,
            RemovalJournalState.Quarantined,
            null!,
        };
        yield return new object[]
        {
            RemovalJournalState.Quarantined,
            null!,
            RemovalJournalState.Deleting,
            null!,
        };
        yield return new object[]
        {
            RemovalJournalState.Deleting,
            null!,
            RemovalJournalState.Deleted,
            RemovalCompletionKind.AutomaticDeletion,
        };
        yield return new object[]
        {
            RemovalJournalState.Quarantined,
            null!,
            RemovalJournalState.Quarantined,
            RemovalCompletionKind.ManualAcknowledgement,
        };
    }

    public static IEnumerable<object[]> InvalidTransitions()
    {
        yield return new object[] { RemovalJournalState.Prepared, RemovalJournalState.Deleting, null! };
        yield return new object[] { RemovalJournalState.Prepared, RemovalJournalState.Deleted, RemovalCompletionKind.AutomaticDeletion };
        yield return new object[] { RemovalJournalState.Quarantined, RemovalJournalState.Deleted, RemovalCompletionKind.AutomaticDeletion };
        yield return new object[] { RemovalJournalState.Deleting, RemovalJournalState.Quarantined, null! };
        yield return new object[] { RemovalJournalState.Deleted, RemovalJournalState.Deleted, RemovalCompletionKind.AutomaticDeletion };
        yield return new object[] { RemovalJournalState.Prepared, RemovalJournalState.Prepared, RemovalCompletionKind.ManualAcknowledgement };
    }

    public static IEnumerable<object[]> DurabilityFaults()
    {
        yield return new object[] { JournalFaultPoint.BeforeTempFlush };
        yield return new object[] { JournalFaultPoint.AfterTempFlush };
        yield return new object[] { JournalFaultPoint.BeforeAtomicReplace };
        yield return new object[] { JournalFaultPoint.AfterAtomicReplace };
        yield return new object[] { JournalFaultPoint.BeforeParentFlush };
    }

    public static IEnumerable<object[]> CorruptJson()
    {
        yield return new object[] { "{}" };
        yield return new object[] { "{\"schemaVersion\":1" };
        yield return new object[] { "{\"schemaVersion\":1,\"schemaVersion\":1}" };
        yield return new object[] { "{\"schemaVersion\":0}" };
        yield return new object[] { "{\"schemaVersion\":2}" };
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public async Task CompareExchange_AllowsOnlyFrozenEdgesAndIncrementsRevisionOnce(
        RemovalJournalState from,
        RemovalCompletionKind? fromCompletion,
        RemovalJournalState to,
        RemovalCompletionKind? toCompletion)
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var prepared = await fixture.CreatePreparedAsync();
        var current = await fixture.AdvanceToAsync(prepared, from, fromCompletion);
        var changedAt = current.Record.StateChangedAtUtc.AddSeconds(1);
        var updated = current.Record with
        {
            State = to,
            CompletionKind = toCompletion,
            JournalRevision = current.Record.JournalRevision + 1,
            StateChangedAtUtc = changedAt,
        };

        var result = await fixture.Journal.CompareExchangeAsync(current, updated);

        Assert.True(result.Succeeded);
        Assert.Equal(RemovalJournalError.None, result.Error);
        Assert.Equal(to, result.Value.Record.State);
        Assert.Equal(toCompletion, result.Value.Record.CompletionKind);
        Assert.Equal(current.Record.JournalRevision + 1, result.Value.Record.JournalRevision);
        Assert.NotEqual(current.CasToken, result.Value.CasToken);
    }

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public async Task CompareExchange_RejectsEveryOtherStateOrDispositionEdge(
        RemovalJournalState from,
        RemovalJournalState to,
        RemovalCompletionKind? completion)
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var prepared = await fixture.CreatePreparedAsync();
        var current = await fixture.AdvanceToAsync(
            prepared,
            from,
            from == RemovalJournalState.Deleted
                ? RemovalCompletionKind.AutomaticDeletion
                : null);
        var updated = current.Record with
        {
            State = to,
            CompletionKind = completion,
            JournalRevision = current.Record.JournalRevision + 1,
            StateChangedAtUtc = current.Record.StateChangedAtUtc.AddSeconds(1),
        };

        var result = await fixture.Journal.CompareExchangeAsync(current, updated);

        Assert.False(result.Succeeded);
        Assert.Equal(RemovalJournalError.InvalidTransition, result.Error);
        var unchanged = await fixture.Journal.ReadAsync(current.Record.OperationId);
        Assert.True(unchanged.Succeeded);
        Assert.Equal(current.Record, unchanged.Value.Record);
        Assert.Equal(current.CasToken, unchanged.Value.CasToken);
    }

    [Fact]
    public async Task CompareExchange_RequiresCompleteExpectedRecordAndFullRecordToken()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var current = await fixture.CreatePreparedAsync();
        var validUpdate = current.Record with
        {
            State = RemovalJournalState.Quarantined,
            JournalRevision = 2,
            StateChangedAtUtc = current.Record.StateChangedAtUtc.AddSeconds(1),
        };
        var wrongRecord = current with
        {
            Record = current.Record with { QueueKey = current.Record.QueueKey with { DownloadId = "other" } },
        };
        var wrongToken = current with
        {
            CasToken = new RemovalJournalCasToken(current.CasToken.JournalRevision, new string('0', 64)),
        };

        var recordConflict = await fixture.Journal.CompareExchangeAsync(wrongRecord, validUpdate);
        var tokenConflict = await fixture.Journal.CompareExchangeAsync(wrongToken, validUpdate);

        Assert.Equal(RemovalJournalError.Conflict, recordConflict.Error);
        Assert.Equal(RemovalJournalError.Conflict, tokenConflict.Error);
    }

    [Fact]
    public async Task CompareExchange_ConcurrentSameExpected_AllowsExactlyOneWriter()
    {
        var hooks = new ConcurrentWriteHooks();
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot, hooks);
        var current = await fixture.CreatePreparedAsync();
        hooks.Arm();
        var first = current.Record with
        {
            State = RemovalJournalState.Quarantined,
            JournalRevision = 2,
            StateChangedAtUtc = current.Record.StateChangedAtUtc.AddSeconds(1),
        };
        var second = first with { StateChangedAtUtc = first.StateChangedAtUtc.AddSeconds(1) };

        var results = await Task.WhenAll(
            Task.Run(async () => await fixture.Journal.CompareExchangeAsync(current, first)),
            Task.Run(async () => await fixture.Journal.CompareExchangeAsync(current, second)));

        Assert.Single(results, static result => result.Succeeded);
        Assert.Single(results, static result => result.Error == RemovalJournalError.Conflict);
    }

    [Fact]
    public async Task Create_RequiresSchemaOnePreparedRevisionOneNullCompletionAndCanonicalQuarantine()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var valid = fixture.NewRecord();
        var invalid = new[]
        {
            valid with { SchemaVersion = 0 },
            valid with { SchemaVersion = 2 },
            valid with { State = RemovalJournalState.Quarantined },
            valid with { JournalRevision = 2 },
            valid with { CompletionKind = RemovalCompletionKind.ManualAcknowledgement },
            valid with { QuarantineRelativePath = RelativeStagingPath.Create("wrong/place") },
            valid with { Identity = valid.Identity with { Version = 0 } },
            valid with { Identity = valid.Identity with { MarkerHex = "not-256-bit-lowercase-hex" } },
            valid with { Identity = valid.Identity with { RootId = "different-root" } },
        };

        foreach (var record in invalid)
        {
            var result = await fixture.Journal.CreateAsync(record);
            Assert.False(result.Succeeded);
            Assert.Equal(RemovalJournalError.Corrupt, result.Error);
        }

        var created = await fixture.Journal.CreateAsync(valid);
        Assert.True(created.Succeeded);
        var duplicate = await fixture.Journal.CreateAsync(valid);
        Assert.False(duplicate.Succeeded);
        Assert.Equal(RemovalJournalError.Conflict, duplicate.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../escape")]
    [InlineData("child/../escape")]
    [InlineData("./child")]
    public void RelativeStagingPath_RejectsBlankOrTraversal(string value)
    {
        Assert.Throws<ArgumentException>(() => RelativeStagingPath.Create(value));
    }

    [Fact]
    public void RelativeStagingPath_RejectsRootedAndMoreThan4096Utf8Bytes()
    {
        Assert.Throws<ArgumentException>(() => RelativeStagingPath.Create(Path.GetFullPath("rooted")));
        Assert.Throws<ArgumentException>(() => RelativeStagingPath.Create(new string('\u00e9', 2049)));
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(RelativeStagingPath.Create(new string('a', 4096)).Value));
    }

    [Fact]
    public void JournalNames_AreFrozenAndConcurrentTempsAreUnique()
    {
        var id = new RemovalOperationId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

        Assert.Equal("00112233445566778899aabbccddeeff.json", FileRemovalJournal.RecordFileName(id));
        Assert.Equal(
            "00112233445566778899aabbccddeeff.json.tmp.42.0123456789abcdef0123456789abcdef",
            FileRemovalJournal.TempFileName(id, 42, "0123456789abcdef0123456789abcdef"));
        Assert.NotEqual(
            FileRemovalJournal.TempFileName(id, Environment.ProcessId, Guid.NewGuid().ToString("N")),
            FileRemovalJournal.TempFileName(id, Environment.ProcessId, Guid.NewGuid().ToString("N")));
        Assert.Equal(".lpc-state/removals", FileRemovalJournal.RelativeJournalDirectory);
        Assert.Equal(".lpc-state/removals.lock", RemovalWriterLease.RelativeLockPath);
    }

    [Fact]
    public async Task WriterLease_IsRootScopedExclusiveAndJournalRejectsAnotherRootsLease()
    {
        await using var firstRoot = await OpenRootAsync(Path.Combine(_fixtureRoot, "lease-one"));
        await using var secondRoot = await OpenRootAsync(Path.Combine(_fixtureRoot, "lease-two"));
        var first = await RemovalWriterLease.AcquireAsync(firstRoot);
        Assert.True(first.Acquired);
        await using var firstLease = first.Lease!;

        var blocked = await RemovalWriterLease.AcquireAsync(firstRoot);
        Assert.False(blocked.Acquired);
        Assert.Equal("QUEUE_REMOVAL_SECOND_WRITER", blocked.Code);

        var wrongRootOpen = await FileRemovalJournal.OpenAsync(secondRoot, firstLease);
        Assert.False(wrongRootOpen.Succeeded);
        Assert.Equal(RemovalJournalError.SecondWriter, wrongRootOpen.Error);
    }

    [SkippableFact]
    public async Task WriterLease_RejectsDanglingLockPathLinkWithoutCreatingOutsideTarget()
    {
        var rootPath = Path.Combine(_fixtureRoot, "dangling-lock-root");
        await using var root = await OpenRootAsync(rootPath);
        var stateDirectory = Path.Combine(rootPath, ".lpc-state");
        Directory.CreateDirectory(stateDirectory);
        var lockPath = Path.Combine(rootPath, RemovalWriterLease.RelativeLockPath.Replace('/', Path.DirectorySeparatorChar));
        var outsideTarget = Path.Combine(_fixtureRoot, "outside-lock-target");
        try
        {
            File.CreateSymbolicLink(lockPath, outsideTarget);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Skip.If(true, "File symlink creation is unavailable on this host.");
        }

        var acquired = await RemovalWriterLease.AcquireAsync(root);

        Assert.False(acquired.Acquired);
        Assert.Equal(HostBridgeQueueResultCodes.RemovalJournalFailure, acquired.Code);
        Assert.False(File.Exists(outsideTarget));
    }

    [Theory]
    [MemberData(nameof(DurabilityFaults))]
    public async Task TransitionFault_LeavesACompleteOldOrNewRecordAndNoTempMapping(
        JournalFaultPoint faultPoint)
    {
        var hooks = new ThrowingHooks(faultPoint);
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot, hooks);
        var current = await fixture.CreatePreparedAsync();
        hooks.Arm();
        var updated = current.Record with
        {
            State = RemovalJournalState.Quarantined,
            JournalRevision = 2,
            StateChangedAtUtc = current.Record.StateChangedAtUtc.AddSeconds(1),
        };

        var result = await fixture.Journal.CompareExchangeAsync(current, updated);

        Assert.False(result.Succeeded);
        Assert.Equal(RemovalJournalError.DurabilityFailure, result.Error);
        var bytes = File.ReadAllBytes(fixture.RecordPath(current.Record.OperationId));
        using var parsed = JsonDocument.Parse(bytes);
        var revision = parsed.RootElement.GetProperty("journalRevision").GetInt64();
        Assert.Contains(revision, new long[] { 1, 2 });
        Assert.Empty(Directory.EnumerateFiles(fixture.JournalDirectory, "*.tmp.*"));
    }

    [Theory]
    [InlineData(JournalFaultPoint.BeforeCompactionUnlink)]
    [InlineData(JournalFaultPoint.BeforeCompactionParentFlush)]
    public async Task CompactionFault_NeverLosesBothDurableMappingAndCompletedRecord(
        JournalFaultPoint faultPoint)
    {
        var hooks = new ThrowingHooks(faultPoint);
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot, hooks);
        var entry = await fixture.CreatePreparedAsync();
        entry = await fixture.AdvanceToAsync(entry, RemovalJournalState.Deleted, RemovalCompletionKind.AutomaticDeletion);
        hooks.Arm();

        var result = await fixture.Journal.CompactAsync(entry);

        Assert.False(result.Succeeded);
        Assert.Equal(RemovalJournalError.DurabilityFailure, result.Error);
        Assert.True(File.Exists(fixture.RecordPath(entry.Record.OperationId)));
        var read = await fixture.Journal.ReadAsync(entry.Record.OperationId);
        Assert.True(read.Succeeded);
        Assert.Equal(entry.Record, read.Value.Record);
    }

    [Fact]
    public async Task Compact_AcceptsOnlyExactAutomaticDeletedOrManualQuarantined()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var prepared = await fixture.CreatePreparedAsync();
        var rejected = await fixture.Journal.CompactAsync(prepared);
        Assert.Equal(RemovalJournalError.InvalidTransition, rejected.Error);

        var automatic = await fixture.AdvanceToAsync(
            prepared,
            RemovalJournalState.Deleted,
            RemovalCompletionKind.AutomaticDeletion);
        var compacted = await fixture.Journal.CompactAsync(automatic);
        Assert.True(compacted.Succeeded);
        Assert.True(compacted.Value);
        Assert.False(File.Exists(fixture.RecordPath(automatic.Record.OperationId)));

        var second = await fixture.CreatePreparedAsync();
        var manual = await fixture.AdvanceToAsync(
            second,
            RemovalJournalState.Quarantined,
            RemovalCompletionKind.ManualAcknowledgement);
        Assert.True((await fixture.Journal.CompactAsync(manual)).Succeeded);
    }

    [Theory]
    [MemberData(nameof(CorruptJson))]
    public async Task Scan_FailsClosedForTruncatedMissingDuplicateOrWrongSchemaJson(string json)
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        File.WriteAllText(fixture.RecordPath(RemovalOperationId.New()), json);

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.Corrupt, scan.Error);
        Assert.Empty(scan.Entries);
    }

    [Fact]
    public async Task Scan_FailsClosedForNullNestedIdentityField()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var created = await fixture.CreatePreparedAsync();
        var path = fixture.RecordPath(created.Record.OperationId);
        var json = File.ReadAllText(path).Replace(
            "\"markerHex\":\"" + new string('a', 64) + "\"",
            "\"markerHex\":null",
            StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.Corrupt, scan.Error);
        Assert.Empty(scan.Entries);
    }

    [Fact]
    public async Task Scan_FailsClosedWhenFilenameAndEmbeddedOperationIdDiffer()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var created = await fixture.CreatePreparedAsync();
        var json = File.ReadAllText(fixture.RecordPath(created.Record.OperationId));
        File.WriteAllText(fixture.RecordPath(RemovalOperationId.New()), json);

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.Corrupt, scan.Error);
    }

    [Fact]
    public async Task Scan_FailsClosedForNoncanonicalFilename()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        File.WriteAllText(Path.Combine(fixture.JournalDirectory, "uppercase.JSON"), "{}");

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.Corrupt, scan.Error);
    }

    [Fact]
    public async Task Scan_FailsClosedForDuplicatePropertyInOtherwiseCompleteRecord()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        var created = await fixture.CreatePreparedAsync();
        var path = fixture.RecordPath(created.Record.OperationId);
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Insert(json.IndexOf('{') + 1, "\"schemaVersion\":1,"));

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.Corrupt, scan.Error);
    }

    [Fact]
    public async Task Scan_RejectsRecordLargerThan64KiB()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        File.WriteAllBytes(
            fixture.RecordPath(RemovalOperationId.New()),
            Enumerable.Repeat((byte)' ', 65_537).ToArray());

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.BoundsExceeded, scan.Error);
    }

    [Fact]
    public async Task Scan_RejectsMoreThanTenThousandRecordsBeforeParsing()
    {
        await using var fixture = await JournalFixture.CreateAsync(_fixtureRoot);
        for (var index = 0; index < 10_001; index++)
        {
            File.WriteAllText(
                Path.Combine(fixture.JournalDirectory, index.ToString("x32") + ".json"),
                "{}");
        }

        var scan = await fixture.Journal.ScanAsync();

        Assert.False(scan.Succeeded);
        Assert.Equal(RemovalJournalError.BoundsExceeded, scan.Error);
        Assert.Empty(scan.Entries);
    }

    [Fact]
    public async Task Open_RejectsLinkedJournalControlPath()
    {
        var rootPath = Path.Combine(_fixtureRoot, "linked-control");
        var target = Path.Combine(_fixtureRoot, "outside-control");
        Directory.CreateDirectory(Path.Combine(rootPath, ".lpc-state"));
        Directory.CreateDirectory(target);
        var link = Path.Combine(rootPath, FileRemovalJournal.RelativeJournalDirectory.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        await using var root = await OpenRootAsync(rootPath);
        var leaseResult = await RemovalWriterLease.AcquireAsync(root);
        Assert.True(leaseResult.Acquired);
        await using var lease = leaseResult.Lease!;

        var opened = await FileRemovalJournal.OpenAsync(root, lease);

        Assert.False(opened.Succeeded);
        Assert.Equal(RemovalJournalError.Corrupt, opened.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixtureRoot))
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
    }

    private static async ValueTask<SafeOwnedRoot> OpenRootAsync(string path)
    {
        Directory.CreateDirectory(path);
        var opened = await SafeOwnedRoot.OpenAsync(path, Path.GetDirectoryName(path)!);
        Assert.True(opened.Opened, opened.Code);
        Assert.NotNull(opened.Root);
        return opened.Root!;
    }

    public enum JournalFaultPoint
    {
        None,
        BeforeTempFlush,
        AfterTempFlush,
        BeforeAtomicReplace,
        AfterAtomicReplace,
        BeforeParentFlush,
        BeforeCompactionUnlink,
        BeforeCompactionParentFlush,
    }

    private sealed class ThrowingHooks : IRemovalJournalDurabilityHooks
    {
        private readonly JournalFaultPoint _point;
        private bool _armed;

        public ThrowingHooks(JournalFaultPoint point) => _point = point;

        public void Arm() => _armed = true;

        public void BeforeTempFlush(string path) => ThrowIf(JournalFaultPoint.BeforeTempFlush);

        public void AfterTempFlush(string path) => ThrowIf(JournalFaultPoint.AfterTempFlush);

        public void BeforeAtomicReplace(string temp, string destination) => ThrowIf(JournalFaultPoint.BeforeAtomicReplace);

        public void AfterAtomicReplace(string destination) => ThrowIf(JournalFaultPoint.AfterAtomicReplace);

        public void BeforeParentFlush(string directory) => ThrowIf(JournalFaultPoint.BeforeParentFlush);

        public void BeforeCompactionUnlink(string path) => ThrowIf(JournalFaultPoint.BeforeCompactionUnlink);

        public void BeforeCompactionParentFlush(string directory) => ThrowIf(JournalFaultPoint.BeforeCompactionParentFlush);

        private void ThrowIf(JournalFaultPoint point)
        {
            if (_armed && _point == point)
            {
                _armed = false;
                throw new IOException("injected durability fault at " + point);
            }
        }
    }

    private sealed class ConcurrentWriteHooks : IRemovalJournalDurabilityHooks
    {
        private readonly CountdownEvent _writers = new(2);
        private bool _armed;

        public void Arm() => _armed = true;
        public void BeforeTempFlush(string path)
        {
            if (!_armed) return;
            _writers.Signal();
            _writers.Wait(TimeSpan.FromSeconds(1));
        }

        public void AfterTempFlush(string path) { }
        public void BeforeAtomicReplace(string temp, string destination) { }
        public void AfterAtomicReplace(string destination) { }
        public void BeforeParentFlush(string directory) { }
        public void BeforeCompactionUnlink(string path) { }
        public void BeforeCompactionParentFlush(string directory) { }
    }

    private sealed class JournalFixture : IAsyncDisposable
    {
        private JournalFixture(
            string rootPath,
            SafeOwnedRoot root,
            RemovalWriterLease lease,
            FileRemovalJournal journal)
        {
            RootPath = rootPath;
            Root = root;
            Lease = lease;
            Journal = journal;
        }

        public string RootPath { get; }

        public string JournalDirectory => Path.Combine(
            RootPath,
            FileRemovalJournal.RelativeJournalDirectory.Replace('/', Path.DirectorySeparatorChar));

        public SafeOwnedRoot Root { get; }

        public RemovalWriterLease Lease { get; }

        public FileRemovalJournal Journal { get; }

        public static async ValueTask<JournalFixture> CreateAsync(
            string fixtureRoot,
            IRemovalJournalDurabilityHooks? hooks = null)
        {
            var rootPath = Path.Combine(fixtureRoot, "root-" + Guid.NewGuid().ToString("N"));
            var root = await OpenRootAsync(rootPath);
            var acquired = await RemovalWriterLease.AcquireAsync(root);
            Assert.True(acquired.Acquired, acquired.Code);
            Assert.NotNull(acquired.Lease);
            var opened = await FileRemovalJournal.OpenAsync(root, acquired.Lease!, hooks);
            Assert.True(opened.Succeeded);
            Assert.NotNull(opened.Value);
            return new JournalFixture(rootPath, root, acquired.Lease!, opened.Value!);
        }

        public RemovalJournalRecord NewRecord()
        {
            var operationId = RemovalOperationId.New();
            var now = DateTime.UtcNow;
            return new RemovalJournalRecord(
                SchemaVersion: 1,
                OperationId: operationId,
                QueueKey: new HostBridgeQueueMutationKey("download", Guid.NewGuid(), 7),
                SourceRelativePath: RelativeStagingPath.Create("attempts/download"),
                QuarantineRelativePath: RelativeStagingPath.Create(
                    ".lpc-trash/" + operationId),
                Identity: new FileIdentity(
                    Version: 1,
                    MarkerHex: new string('a', 64),
                    RootId: Root.RootId,
                    EntryType: FileIdentityEntryType.Directory),
                Capability: StagingDeletionCapability.ProtectedRootCleanup,
                State: RemovalJournalState.Prepared,
                CompletionKind: null,
                JournalRevision: 1,
                CreatedAtUtc: now,
                StateChangedAtUtc: now);
        }

        public async ValueTask<RemovalJournalEntry> CreatePreparedAsync()
        {
            var created = await Journal.CreateAsync(NewRecord());
            Assert.True(created.Succeeded);
            Assert.Equal(RemovalJournalError.None, created.Error);
            return created.Value;
        }

        public async ValueTask<RemovalJournalEntry> AdvanceToAsync(
            RemovalJournalEntry entry,
            RemovalJournalState target,
            RemovalCompletionKind? completion)
        {
            var states = target switch
            {
                RemovalJournalState.Prepared => Array.Empty<(RemovalJournalState, RemovalCompletionKind?)>(),
                RemovalJournalState.Quarantined when completion == RemovalCompletionKind.ManualAcknowledgement =>
                    new[]
                    {
                        (RemovalJournalState.Quarantined, (RemovalCompletionKind?)null),
                        (RemovalJournalState.Quarantined, completion),
                    },
                RemovalJournalState.Quarantined =>
                    new[] { (RemovalJournalState.Quarantined, (RemovalCompletionKind?)null) },
                RemovalJournalState.Deleting =>
                    new[]
                    {
                        (RemovalJournalState.Quarantined, (RemovalCompletionKind?)null),
                        (RemovalJournalState.Deleting, (RemovalCompletionKind?)null),
                    },
                RemovalJournalState.Deleted =>
                    new[]
                    {
                        (RemovalJournalState.Quarantined, (RemovalCompletionKind?)null),
                        (RemovalJournalState.Deleting, (RemovalCompletionKind?)null),
                        (RemovalJournalState.Deleted, completion),
                    },
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };

            foreach (var (state, disposition) in states)
            {
                var updated = entry.Record with
                {
                    State = state,
                    CompletionKind = disposition,
                    JournalRevision = entry.Record.JournalRevision + 1,
                    StateChangedAtUtc = entry.Record.StateChangedAtUtc.AddSeconds(1),
                };
                var result = await Journal.CompareExchangeAsync(entry, updated);
                Assert.True(result.Succeeded);
                entry = result.Value;
            }

            return entry;
        }

        public string RecordPath(RemovalOperationId operationId) =>
            Path.Combine(JournalDirectory, FileRemovalJournal.RecordFileName(operationId));

        public async ValueTask DisposeAsync()
        {
            await Journal.DisposeAsync();
            await Lease.DisposeAsync();
            await Root.DisposeAsync();
        }
    }
}
