using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

public sealed class HostBridgeQueuePersistenceV2Tests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "queue-v2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort */ }
    }

    private string TempFile()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".json");
    }

    private static HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> V2(
        string path,
        Action<string>? onWarn = null,
        Func<HostBridgeDownloadItemDto, HostBridgeRestartEvidence>? evidence = null,
        Func<DateTime>? clock = null) =>
        new(
            persistencePath: path,
            onWarn: onWarn,
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                RestartEvidence = evidence ?? (static _ => new(false, false, false)),
                UtcNow = clock ?? (static () => DateTime.UtcNow),
            });

    private string PersistV2Item(
        HostBridgeDownloadAttemptState state,
        DateTime? changedAtUtc = null,
        long revision = 4)
    {
        var path = TempFile();
        var dto = new HostBridgeDownloadItemDto
        {
            DownloadId = "restart",
            AttemptId = Guid.Parse("b7ef2460-f5e7-4c91-9266-5ce763ad0065"),
            Revision = revision,
            AttemptState = state,
            StateChangedAtUtc = changedAtUtc ?? new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc),
        };
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(
            new { schemaVersion = 2, items = new[] { dto } }, options));
        return path;
    }

    private static HostBridgeDownloadItemDto PersistedDto(string id) => new()
    {
        DownloadId = id,
        AttemptId = Guid.Parse("b7ef2460-f5e7-4c91-9266-5ce763ad0065"),
        Revision = 1,
        AttemptState = HostBridgeDownloadAttemptState.Queued,
        StateChangedAtUtc = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc),
    };

    private static byte[] SerializeEnvelope(IReadOnlyCollection<HostBridgeDownloadItemDto> dtos)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, items = dtos }, options);
    }

    [Fact]
    public void AttemptV2_PersistsSchemaEnvelope()
    {
        var path = TempFile();
        var store = V2(path);
        store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "release" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("release", document.RootElement.GetProperty("items")[0].GetProperty("downloadId").GetString());
    }

    [Fact]
    public void V1Array_MigratesAndCollapsesCaseVariantsDeterministically()
    {
        var path = TempFile();
        File.WriteAllText(path, """
[
  {"downloadId":"  ABC  ","status":"Failed","completedAt":"2026-07-11T10:00:00Z"},
  {"downloadId":"abc","status":"Completed","completedAt":"2026-07-11T11:00:00Z"}
]
""");

        var store = V2(path);
        Assert.True(store.TryGet("AbC", out var item));
        Assert.Equal("ABC", item!.DownloadId);
        Assert.Equal(HostBridgeDownloadAttemptState.CompletedImportable, item.AttemptState);
        Assert.Equal(1, item.Revision);
        Assert.NotEqual(Guid.Empty, item.AttemptId);
        Assert.Single(store.GetSnapshot());
        Assert.Contains("\"schemaVersion\":2", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void V1DuplicateSelection_IsIndependentOfInputOrderAndTimestampRecency()
    {
        const string older = "{\"downloadId\":\"Dupe\",\"title\":\"older\",\"status\":\"Completed\",\"completedAt\":\"2026-07-11T09:00:00Z\"}";
        const string newer = "{\"downloadId\":\"dupe\",\"title\":\"newer\",\"status\":\"Completed\",\"completedAt\":\"2026-07-11T13:00:00Z\"}";
        var firstPath = TempFile();
        var secondPath = TempFile();
        File.WriteAllText(firstPath, $"[{older},{newer}]");
        File.WriteAllText(secondPath, $"[{newer},{older}]");

        var first = V2(firstPath).GetSnapshot().Single();
        var second = V2(secondPath).GetSnapshot().Single();

        Assert.Equal(first.DownloadId, second.DownloadId);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.CompletedAt, second.CompletedAt);
        Assert.Equal(first.AttemptId, second.AttemptId);
    }

    [Fact]
    public void V1Migration_IsIdempotentAcrossRestart()
    {
        var path = TempFile();
        File.WriteAllText(path, "[{\"downloadId\":\"stable\",\"status\":\"Completed\"}]");

        var first = V2(path).GetSnapshot().Single();
        var migratedJson = File.ReadAllText(path);
        var second = V2(path).GetSnapshot().Single();

        Assert.Equal(migratedJson, File.ReadAllText(path));
        Assert.Equal(first.AttemptId, second.AttemptId);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.AttemptState, second.AttemptState);
    }

    [Theory]
    [InlineData(false, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(true, HostBridgeDownloadAttemptState.Downloading)]
    public void V1ActiveItem_MigratesThroughRestartEvidence(
        bool verifiedSegments,
        HostBridgeDownloadAttemptState expected)
    {
        var path = TempFile();
        File.WriteAllText(path, "[{\"downloadId\":\"active\",\"status\":\"Downloading\"}]");

        var item = V2(
            path,
            evidence: _ => new(true, verifiedSegments, false))
            .GetSnapshot()
            .Single();

        Assert.Equal(expected, item.AttemptState);
        Assert.Equal(1, item.Revision);
    }

    [Fact]
    public void FutureSchemaVersion_FailsClosedWithoutOverwritingFile()
    {
        var path = TempFile();
        const string future = "{\"schemaVersion\":3,\"items\":[]}";
        File.WriteAllText(path, future);
        var warnings = new List<string>();

        var store = V2(path, onWarn: warnings.Add);
        Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "in-memory-only" }).Applied);

        Assert.Single(store.GetSnapshot());
        Assert.Equal(future, File.ReadAllText(path));
        Assert.Contains(warnings, x => x.Contains("newer schemaVersion 3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public void EmptyOrWhitespaceV2_FailsClosedAndLaterMutationPreservesBytes(string malformed)
    {
        var path = TempFile();
        File.WriteAllText(path, malformed);
        var warnings = new List<string>();

        var store = V2(path, warnings.Add);
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "memory-only" });
        var transitioned = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);

        Assert.True(transitioned.Applied);
        Assert.Equal(malformed, File.ReadAllText(path));
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void MalformedNonemptyV2_LaterMutationPreservesOriginalBytes()
    {
        var path = TempFile();
        const string malformed = "{not-json";
        File.WriteAllText(path, malformed);

        var store = V2(path);
        Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "memory-only" }).Applied);

        Assert.Equal(malformed, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"2\",\"items\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"items\":{}}")]
    [InlineData("{\"schemaVersion\":2,\"items\":[{\"downloadId\":\"bad\",\"attemptId\":\"b7ef2460-f5e7-4c91-9266-5ce763ad0065\",\"revision\":1,\"attemptState\":99,\"stateChangedAtUtc\":\"2026-07-11T12:00:00Z\"}]}")]
    public void MalformedV2_FailsClosedWithoutRewriting(string malformed)
    {
        var path = TempFile();
        File.WriteAllText(path, malformed);
        var warnings = new List<string>();

        var store = V2(path, warnings.Add);

        Assert.Empty(store.GetSnapshot());
        Assert.Equal(malformed, File.ReadAllText(path));
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void InvalidPersistedStatus_FailsWholeV2SnapshotClosed()
    {
        var path = TempFile();
        const string invalid = "{\"schemaVersion\":2,\"items\":[{\"downloadId\":\"would-be-valid\",\"status\":\"Completed\",\"attemptId\":\"c7ef2460-f5e7-4c91-9266-5ce763ad0065\",\"revision\":1,\"attemptState\":\"CompletedImportable\",\"stateChangedAtUtc\":\"2026-07-11T12:00:00Z\"},{\"downloadId\":\"bad-status\",\"status\":99,\"attemptId\":\"b7ef2460-f5e7-4c91-9266-5ce763ad0065\",\"revision\":1,\"attemptState\":\"Queued\",\"stateChangedAtUtc\":\"2026-07-11T12:00:00Z\"}]}";
        File.WriteAllText(path, invalid);

        var store = V2(path);
        Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "memory-only" }).Applied);

        Assert.Single(store.GetSnapshot());
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidPersistedAttemptState_FailsV1MigrationClosed()
    {
        var path = TempFile();
        const string invalid = "[{\"downloadId\":\"bad-attempt-state\",\"status\":\"Completed\",\"attemptState\":99}]";
        File.WriteAllText(path, invalid);

        var store = V2(path);
        Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "memory-only" }).Applied);

        Assert.Single(store.GetSnapshot());
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    [Fact]
    public void V1Migration_MultiIdReorderProducesIdenticalBytesAndAttemptIds()
    {
        const string alpha = "{\"downloadId\":\"alpha\",\"status\":\"Completed\"}";
        const string bravo = "{\"downloadId\":\"bravo\",\"status\":\"Failed\"}";
        const string echo = "{\"downloadId\":\"echo\",\"status\":\"Cancelled\"}";
        const string mike = "{\"downloadId\":\"mike\",\"status\":\"Completed\"}";
        const string xray = "{\"downloadId\":\"xray\",\"status\":\"Failed\"}";
        const string zulu = "{\"downloadId\":\"zulu\",\"status\":\"Cancelled\"}";
        var firstPath = TempFile();
        var secondPath = TempFile();
        File.WriteAllText(firstPath, $"[{zulu},{alpha},{mike},{bravo},{xray},{echo}]");
        File.WriteAllText(secondPath, $"[{echo},{xray},{bravo},{mike},{alpha},{zulu}]");

        var first = V2(firstPath).GetSnapshot().OrderBy(x => x.DownloadId).ToArray();
        var second = V2(secondPath).GetSnapshot().OrderBy(x => x.DownloadId).ToArray();

        Assert.Equal(File.ReadAllText(firstPath), File.ReadAllText(secondPath));
        Assert.Equal(first.Select(x => x.AttemptId), second.Select(x => x.AttemptId));

        using var document = JsonDocument.Parse(File.ReadAllText(firstPath));
        Assert.Equal(
            new[] { "alpha", "bravo", "echo", "mike", "xray", "zulu" },
            document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("downloadId").GetString()));
    }

    [Fact]
    public void V1Migration_CanonicalDtoHasPinnedAttemptId()
    {
        var path = TempFile();
        File.WriteAllText(path, "[{\"downloadId\":\"stable\",\"status\":\"Completed\"}]");

        var migrated = V2(path).GetSnapshot().Single();

        Assert.Equal(Guid.Parse("a4c7a5d1-209b-e063-22ec-a4e106c71689"), migrated.AttemptId);
    }

    [Fact]
    public void V1Migration_OffsetDatesHaveUtcInvariantPinnedAttemptId()
    {
        var path = TempFile();
        File.WriteAllText(path, "[{\"downloadId\":\"offset\",\"status\":\"Completed\",\"startedAt\":\"2026-07-11T08:30:00-04:00\",\"completedAt\":\"2026-07-11T09:45:00-04:00\"}]");

        var migrated = V2(path).GetSnapshot().Single();

        Assert.Equal(Guid.Parse("0a21e1cc-14f8-dc2b-0225-c4407ede4908"), migrated.AttemptId);
        Assert.Equal(new DateTime(2026, 7, 11, 13, 45, 0, DateTimeKind.Utc), migrated.StateChangedAtUtc);
    }

    [Fact]
    public void V1DuplicatePrecedence_PrefersFailedThenCancelledThenActive()
    {
        var path = TempFile();
        File.WriteAllText(path, """
[
  {"downloadId":"same","title":"active","status":"Downloading"},
  {"downloadId":"SAME","title":"cancelled","status":"Cancelled"},
  {"downloadId":" same ","title":"failed","status":"Failed"}
]
""");

        var migrated = V2(path, evidence: _ => new(true, true, false)).GetSnapshot().Single();

        Assert.Equal("failed", migrated.Title);
        Assert.Equal(HostBridgeDownloadAttemptState.Failed, migrated.AttemptState);
    }

    [Theory]
    [InlineData(HostBridgeDownloadAttemptState.Queued, false, false, false, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(HostBridgeDownloadAttemptState.Paused, false, false, false, HostBridgeDownloadAttemptState.Paused)]
    [InlineData(HostBridgeDownloadAttemptState.Preparing, true, false, false, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(HostBridgeDownloadAttemptState.Preparing, false, false, false, HostBridgeDownloadAttemptState.Failed)]
    [InlineData(HostBridgeDownloadAttemptState.Downloading, true, true, false, HostBridgeDownloadAttemptState.Downloading)]
    [InlineData(HostBridgeDownloadAttemptState.Downloading, true, false, false, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(HostBridgeDownloadAttemptState.Finalizing, true, false, true, HostBridgeDownloadAttemptState.CompletedImportable)]
    [InlineData(HostBridgeDownloadAttemptState.Finalizing, true, false, false, HostBridgeDownloadAttemptState.Queued)]
    [InlineData(HostBridgeDownloadAttemptState.Cancelling, true, false, false, HostBridgeDownloadAttemptState.Cancelled)]
    [InlineData(HostBridgeDownloadAttemptState.CompletedImportable, false, false, false, HostBridgeDownloadAttemptState.CompletedImportable)]
    [InlineData(HostBridgeDownloadAttemptState.Failed, false, false, false, HostBridgeDownloadAttemptState.Failed)]
    [InlineData(HostBridgeDownloadAttemptState.Cancelled, false, false, false, HostBridgeDownloadAttemptState.Cancelled)]
    public void Restart_UsesStateAndEvidenceTable(
        HostBridgeDownloadAttemptState persisted,
        bool contained,
        bool segments,
        bool finalized,
        HostBridgeDownloadAttemptState expected)
    {
        var path = PersistV2Item(persisted);
        var store = V2(path, evidence: _ => new(contained, segments, finalized));
        Assert.True(store.TryGet("restart", out var recovered));
        Assert.Equal(expected, recovered!.AttemptState);
    }

    [Fact]
    public void Restart_StateRecoveryDoesNotDependOnPersistedTimestamp()
    {
        var earlyPath = PersistV2Item(
            HostBridgeDownloadAttemptState.Downloading,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var latePath = PersistV2Item(
            HostBridgeDownloadAttemptState.Downloading,
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var early = V2(earlyPath, evidence: _ => new(true, false, false)).GetSnapshot().Single();
        var late = V2(latePath, evidence: _ => new(true, false, false)).GetSnapshot().Single();

        Assert.Equal(HostBridgeDownloadAttemptState.Queued, early.AttemptState);
        Assert.Equal(early.AttemptState, late.AttemptState);
    }

    [Fact]
    public void Restart_ChangedRecoveryPersistsExactlyOneMonotonicMutation()
    {
        var originalTime = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var path = PersistV2Item(HostBridgeDownloadAttemptState.Downloading, originalTime);
        var store = V2(
            path,
            evidence: _ => new(true, false, false),
            clock: () => originalTime.AddHours(-1));

        var recovered = store.GetSnapshot().Single();
        Assert.Equal(5, recovered.Revision);
        Assert.Equal(originalTime.AddTicks(1), recovered.StateChangedAtUtc);

        var restarted = V2(path, evidence: _ => new(false, false, false)).GetSnapshot().Single();
        Assert.Equal(5, restarted.Revision);
        Assert.Equal(originalTime.AddTicks(1), restarted.StateChangedAtUtc);
        Assert.Equal(HostBridgeDownloadAttemptState.Queued, restarted.AttemptState);
    }

    [Fact]
    public void Restart_UnchangedRecoveryLeavesFileByteForByte()
    {
        var path = PersistV2Item(HostBridgeDownloadAttemptState.Queued);
        var original = File.ReadAllText(path);

        var store = V2(path);

        Assert.Single(store.GetSnapshot());
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void OldCompletedImportable_SurvivesSnapshotRetentionSweep()
    {
        var path = TempFile();
        var dto = new HostBridgeDownloadItemDto
        {
            DownloadId = "old-importable",
            Status = HostBridgeDownloadItemStatus.Completed,
            CompletedAt = DateTime.UtcNow.AddYears(-1),
            AttemptId = Guid.Parse("b7ef2460-f5e7-4c91-9266-5ce763ad0065"),
            Revision = 4,
            AttemptState = HostBridgeDownloadAttemptState.CompletedImportable,
            StateChangedAtUtc = DateTime.UtcNow.AddYears(-1),
        };
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 2, items = new[] { dto } }, options));

        Assert.Single(V2(path).GetSnapshot());
    }

    [Fact]
    public void OversizedPersistenceFile_FailsClosedBeforeReadAndPreservesLength()
    {
        var path = TempFile();
        const long oversizedLength = (16L * 1024 * 1024) + 1;
        using (var stream = File.Create(path))
            stream.SetLength(oversizedLength);
        var warnings = new List<string>();

        var store = V2(path, warnings.Add);
        Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "memory-only" }).Applied);

        Assert.Equal(oversizedLength, new FileInfo(path).Length);
        Assert.Contains(warnings, warning => warning.Contains("maximum persistence size", StringComparison.Ordinal));
    }

    [Fact]
    public void TooManyPersistedItems_FailsClosedWithoutCanonicalizingEntries()
    {
        var path = TempFile();
        var oversized = "[" + string.Join(',', Enumerable.Repeat("{}", 10_001)) + "]";
        File.WriteAllText(path, oversized);
        var warnings = new List<string>();

        var store = V2(path, warnings.Add);

        Assert.Empty(store.GetSnapshot());
        Assert.Equal(oversized, File.ReadAllText(path));
        Assert.Contains(warnings, warning => warning.Contains("maximum item count", StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedPersistedString_FailsClosedWithRedactedWarning()
    {
        var path = TempFile();
        var secret = "SECRET-" + new string('x', (32 * 1024) + 1);
        var malformed = JsonSerializer.Serialize(new[]
        {
            new { downloadId = "bounded", title = secret, status = "Completed" },
        });
        File.WriteAllText(path, malformed);
        var warnings = new List<string>();

        var store = V2(path, warnings.Add);

        Assert.Empty(store.GetSnapshot());
        Assert.Equal(malformed, File.ReadAllText(path));
        Assert.Contains(warnings, warning => warning.Contains("maximum string length", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, warning => warning.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactMaximumString_WritesAndReopens()
    {
        var path = TempFile();
        var title = new string('x', 32 * 1024);

        var added = V2(path).TryAddAttempt(new HostBridgeDownloadItem
        {
            DownloadId = "max-string",
            Title = title,
        });
        var reopened = V2(path).GetSnapshot().Single();

        Assert.True(added.Applied);
        Assert.Equal(title, reopened.Title);
    }

    [Theory]
    [InlineData("TryAdd")]
    [InlineData("TryAddAttempt")]
    [InlineData("AddOrReplace")]
    public void OverMaximumString_AllInsertionWrappersRejectWithoutStateOrFile(string insertion)
    {
        var path = TempFile();
        var store = V2(path);
        var item = new HostBridgeDownloadItem
        {
            DownloadId = "too-long",
            Title = new string('x', (32 * 1024) + 1),
        };

        switch (insertion)
        {
            case "TryAdd":
                Assert.False(store.TryAdd(item));
                break;
            case "TryAddAttempt":
                var result = store.TryAddAttempt(item);
                Assert.False(result.Applied);
                Assert.Equal(HostBridgeQueueResultCodes.PersistenceLimitExceeded, result.Code);
                break;
            case "AddOrReplace":
                var error = Assert.Throws<InvalidOperationException>(() => store.AddOrReplace(item));
                Assert.Contains(HostBridgeQueueResultCodes.PersistenceLimitExceeded, error.Message, StringComparison.Ordinal);
                break;
        }

        Assert.Empty(store.GetSnapshot());
        Assert.False(File.Exists(path));
        Assert.Equal(0, item.Revision);
        Assert.Equal(Guid.Empty, item.AttemptId);
    }

    [Fact]
    public void ExactMaximumItemCount_WritesReopensAndRejectsNextItem()
    {
        var path = TempFile();
        var dtos = Enumerable.Range(0, 10_000)
            .Select(index => PersistedDto($"item-{index:D5}"))
            .ToArray();
        File.WriteAllBytes(path, SerializeEnvelope(dtos));
        var store = V2(path);
        store.PersistSnapshot();
        var acceptedBytes = File.ReadAllBytes(path);

        var rejected = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "item-over-limit" });

        Assert.Equal(10_000, store.GetSnapshot().Count());
        Assert.False(rejected.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.PersistenceLimitExceeded, rejected.Code);
        Assert.Equal(acceptedBytes, File.ReadAllBytes(path));
        Assert.Equal(10_000, V2(path).GetSnapshot().Count());
    }

    [Fact]
    public void ExactMaximumEnvelope_ReopensButMutableGrowthBlocksPersistAndTransition()
    {
        const int maxBytes = 16 * 1024 * 1024;
        var path = TempFile();
        var dtos = Enumerable.Range(0, 520)
            .Select(index => PersistedDto($"envelope-{index:D4}"))
            .ToArray();
        var remaining = maxBytes - SerializeEnvelope(dtos).Length;
        foreach (var dto in dtos)
        {
            var length = Math.Min(remaining, 32 * 1024);
            dto.Title = new string('x', length);
            remaining -= length;
        }
        Assert.Equal(0, remaining);
        var exact = SerializeEnvelope(dtos);
        Assert.Equal(maxBytes, exact.Length);
        File.WriteAllBytes(path, exact);
        var warnings = new List<string>();
        var store = V2(path, warnings.Add);
        store.PersistSnapshot();
        var accepted = File.ReadAllBytes(path);
        var item = store.GetSnapshot().First();
        var before = (item.Revision, item.AttemptState, item.StateChangedAtUtc);

        item.TotalSize = long.MinValue;
        store.PersistSnapshot();
        var transitioned = store.TryTransition(item.MutationKey(), HostBridgeDownloadAttemptState.Preparing);

        Assert.Equal(maxBytes, accepted.Length);
        Assert.Equal(accepted, File.ReadAllBytes(path));
        Assert.False(transitioned.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.PersistenceLimitExceeded, transitioned.Code);
        Assert.Equal(before, (item.Revision, item.AttemptState, item.StateChangedAtUtc));
        Assert.Contains(warnings, warning => warning.Contains(HostBridgeQueueResultCodes.PersistenceLimitExceeded, StringComparison.Ordinal));
        item.TotalSize = 0;
        Assert.Equal(520, V2(path).GetSnapshot().Count());
    }

    [Theory]
    [InlineData("TryAdd", "status")]
    [InlineData("TryAddAttempt", "status")]
    [InlineData("AddOrReplace", "status")]
    [InlineData("TryAdd", "nan")]
    [InlineData("TryAddAttempt", "nan")]
    [InlineData("AddOrReplace", "nan")]
    [InlineData("TryAdd", "positive-infinity")]
    [InlineData("TryAddAttempt", "positive-infinity")]
    [InlineData("AddOrReplace", "positive-infinity")]
    [InlineData("TryAdd", "negative-infinity")]
    [InlineData("TryAddAttempt", "negative-infinity")]
    [InlineData("AddOrReplace", "negative-infinity")]
    public void InvalidWriterState_AllInsertionWrappersRejectWithoutMutation(
        string insertion,
        string invalidKind)
    {
        var path = TempFile();
        var store = V2(path);
        var item = new HostBridgeDownloadItem { DownloadId = "invalid-writer" };
        SetInvalidWriterState(item, invalidKind);

        switch (insertion)
        {
            case "TryAdd":
                Assert.False(store.TryAdd(item));
                break;
            case "TryAddAttempt":
                var result = store.TryAddAttempt(item);
                Assert.False(result.Applied);
                Assert.Equal(HostBridgeQueueResultCodes.PersistenceInvalidState, result.Code);
                break;
            case "AddOrReplace":
                var error = Assert.Throws<InvalidOperationException>(() => store.AddOrReplace(item));
                Assert.Contains(HostBridgeQueueResultCodes.PersistenceInvalidState, error.Message, StringComparison.Ordinal);
                break;
        }

        Assert.Empty(store.GetSnapshot());
        Assert.False(File.Exists(path));
        Assert.Equal(Guid.Empty, item.AttemptId);
        Assert.Equal(0, item.Revision);
    }

    [Fact]
    public void InvalidAttemptState_InsertionRejectsWithoutMutation()
    {
        var path = TempFile();
        var item = new HostBridgeDownloadItemDto
        {
            DownloadId = "invalid-attempt-state",
            AttemptId = Guid.NewGuid(),
            Revision = 1,
            AttemptState = (HostBridgeDownloadAttemptState)999,
            StateChangedAtUtc = DateTime.UtcNow,
        }.ToItem();

        var result = V2(path).TryAddAttempt(item);

        Assert.False(result.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.PersistenceInvalidState, result.Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void InvalidAttemptState_TransitionReturnsPersistenceInvalidState()
    {
        var path = TempFile();
        var store = V2(path);
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "invalid-transition-state" });
        var item = added.Item!;
        var persisted = File.ReadAllBytes(path);
        item.RestoreAttempt(
            item.AttemptId,
            item.Revision,
            (HostBridgeDownloadAttemptState)999,
            item.StateChangedAtUtc);
        var metadata = (item.Revision, item.AttemptState, item.StateChangedAtUtc);

        var result = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);

        Assert.False(result.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.PersistenceInvalidState, result.Code);
        Assert.Equal(metadata, (item.Revision, item.AttemptState, item.StateChangedAtUtc));
        Assert.Equal(persisted, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("nan")]
    [InlineData("positive-infinity")]
    [InlineData("negative-infinity")]
    public void InvalidWriterState_TransitionRejectsThenValidStateWritesAndReopens(string invalidKind)
    {
        var path = TempFile();
        var warnings = new List<string>();
        var store = V2(path, warnings.Add);
        var added = store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "transition-invalid" });
        var item = added.Item!;
        var persisted = File.ReadAllBytes(path);
        var metadata = (item.Revision, item.AttemptState, item.StateChangedAtUtc);
        SetInvalidWriterState(item, invalidKind);

        store.PersistSnapshot();

        var rejected = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);

        Assert.False(rejected.Applied);
        Assert.Equal(HostBridgeQueueResultCodes.PersistenceInvalidState, rejected.Code);
        Assert.Equal(metadata, (item.Revision, item.AttemptState, item.StateChangedAtUtc));
        Assert.Equal(persisted, File.ReadAllBytes(path));
        Assert.Contains(warnings, warning => warning.Contains(
            HostBridgeQueueResultCodes.PersistenceInvalidState,
            StringComparison.Ordinal));

        item.SetStatus(HostBridgeDownloadItemStatus.Queued);
        item.SetProgress(1);
        var accepted = store.TryTransition(added.Current, HostBridgeDownloadAttemptState.Preparing);
        Assert.True(accepted.Applied);
        Assert.Equal(HostBridgeDownloadAttemptState.Queued, V2(
            path,
            evidence: _ => new(true, false, false)).GetSnapshot().Single().AttemptState);
    }

    private static void SetInvalidWriterState(HostBridgeDownloadItem item, string invalidKind)
    {
        switch (invalidKind)
        {
            case "status":
                item.SetStatus((HostBridgeDownloadItemStatus)999);
                break;
            case "nan":
                item.SetProgress(double.NaN);
                break;
            case "positive-infinity":
                item.SetProgress(double.PositiveInfinity);
                break;
            case "negative-infinity":
                item.SetProgress(double.NegativeInfinity);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidKind));
        }
    }

    [Fact]
    public void AttemptV2_TransientReplaceFailureLeavesOriginalEnvelopeAndCleansTempFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = TempFile();
        var warnings = new List<string>();
        var store = V2(path, warnings.Add);
        Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "original" }).Applied);
        var original = File.ReadAllText(path);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(store.TryAddAttempt(new HostBridgeDownloadItem { DownloadId = "blocked" }).Applied);
        }

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Contains(warnings, warning => warning.Contains("after 5 replace attempts", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp"));
    }
}
