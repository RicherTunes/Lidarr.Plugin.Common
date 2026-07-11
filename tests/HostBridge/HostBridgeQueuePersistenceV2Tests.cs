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
