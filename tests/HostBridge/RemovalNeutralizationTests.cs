using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class RemovalNeutralizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "removal-neutralization-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeleteData_IsDeferredAfterBoundedCancellation()
    {
        var fixture = DownloadingFixture();

        var result = await fixture.Store.RemoveAttemptAsync(
            fixture.Key,
            deleteData: true,
            fixture.StopWorker,
            TimeSpan.FromSeconds(1));

        Assert.Equal(HostBridgeQueueResultCodes.RemovalDeferred, result.Code);
        Assert.True(result.MappingRetained);
        Assert.False(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.True(fixture.Store.TryGet(fixture.Key.DownloadId, out _));
        Assert.True(Directory.Exists(fixture.OutputPath));
        Assert.True(File.Exists(Path.Combine(fixture.OutputPath, "partial.flac")));
    }

    [Fact]
    public async Task DeleteDataFalse_RemovesQueueOnly()
    {
        var fixture = TerminalFixture();

        var result = await fixture.Store.RemoveAttemptAsync(
            fixture.Key,
            deleteData: false,
            static (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(1));

        Assert.True(result.StateRemoved);
        Assert.False(result.FilesRemoved);
        Assert.False(result.MappingRetained);
        Assert.False(fixture.Store.TryGet(fixture.Key.DownloadId, out _));
        Assert.True(Directory.Exists(fixture.OutputPath));
        Assert.True(File.Exists(Path.Combine(fixture.OutputPath, "completed.flac")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Fixture DownloadingFixture()
    {
        var fixture = CreateFixture("downloading", "partial.flac");
        var preparing = fixture.Store.TryTransition(
            fixture.Key,
            HostBridgeDownloadAttemptState.Preparing);
        var downloading = fixture.Store.TryTransition(
            preparing.Current,
            HostBridgeDownloadAttemptState.Downloading);
        return fixture with
        {
            Key = downloading.Current,
            StopWorker = static (_, _) => Task.CompletedTask,
        };
    }

    private Fixture TerminalFixture()
    {
        var fixture = CreateFixture("terminal", "completed.flac");
        var failed = fixture.Store.TryTransition(
            fixture.Key,
            HostBridgeDownloadAttemptState.Failed);
        return fixture with { Key = failed.Current };
    }

    private Fixture CreateFixture(string id, string evidenceName)
    {
        var stagingRoot = Path.Combine(_root, "staging");
        var outputPath = Path.Combine(stagingRoot, id);
        Directory.CreateDirectory(outputPath);
        File.WriteAllText(Path.Combine(outputPath, evidenceName), "retain");
        var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>(
            persistencePath: Path.Combine(_root, "queue.json"),
            options: new HostBridgeQueueStoreOptions
            {
                ContractVersion = HostBridgeQueueContractVersion.AttemptV2,
                OwnedStagingRoot = stagingRoot,
            });
        var added = store.TryAddAttempt(new HostBridgeDownloadItem
        {
            DownloadId = id,
            OutputPath = outputPath,
        });
        return new Fixture(store, added.Current, outputPath, static (_, _) => Task.CompletedTask);
    }

    private sealed record Fixture(
        HostBridgeDownloadTrackerStore<HostBridgeDownloadItem> Store,
        HostBridgeQueueMutationKey Key,
        string OutputPath,
        Func<HostBridgeDownloadItem, System.Threading.CancellationToken, Task> StopWorker);
}
