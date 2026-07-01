using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

public sealed class TerminalReleaseSuppressionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public TerminalReleaseSuppressionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lpc-terminal-suppression-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string PathFor(string fileName = "suppressed-releases.json") => Path.Combine(_tempDir, fileName);

    [Fact]
    public void IsSuppressed_UnknownRelease_ReturnsFalse()
    {
        var sut = new TerminalReleaseSuppressionStore(PathFor(), "Qobuzarr");

        Assert.False(sut.IsSuppressed("album-not-suppressed"));
    }

    [Fact]
    public async Task SuppressAsync_PersistsAcrossInstances()
    {
        var path = PathFor();
        var first = new TerminalReleaseSuppressionStore(path, "Qobuzarr");
        await first.SuppressAsync("album-durable", "track-1", "Restricted");

        var second = new TerminalReleaseSuppressionStore(path, "Qobuzarr");

        Assert.True(second.IsSuppressed("album-durable"));
    }

    [Fact]
    public async Task SuppressAsync_NormalizesTrimAndCase()
    {
        var sut = new TerminalReleaseSuppressionStore(PathFor(), "Qobuzarr");

        await sut.SuppressAsync("  ALBUM-123  ", "track-1", "Restricted");

        Assert.True(sut.IsSuppressed("album-123"));
        Assert.True(sut.IsSuppressed(" ALBUM-123 "));
    }

    [Fact]
    public async Task SuppressAsync_BeyondMaxEntries_EvictsOldestEntry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new TerminalReleaseSuppressionStore(PathFor(), "Qobuzarr", maxEntries: 3, clock: clock);

        await sut.SuppressAsync("album-1", "track-1", "Restricted");
        clock.Advance(TimeSpan.FromMinutes(1));
        await sut.SuppressAsync("album-2", "track-2", "Restricted");
        clock.Advance(TimeSpan.FromMinutes(1));
        await sut.SuppressAsync("album-3", "track-3", "Restricted");
        clock.Advance(TimeSpan.FromMinutes(1));
        await sut.SuppressAsync("album-4", "track-4", "Restricted");

        Assert.False(sut.IsSuppressed("album-1"));
        Assert.True(sut.IsSuppressed("album-2"));
        Assert.True(sut.IsSuppressed("album-3"));
        Assert.True(sut.IsSuppressed("album-4"));
    }

    [Fact]
    public async Task IsSuppressed_AfterTtlExpiry_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ttl = TimeSpan.FromDays(30);
        var sut = new TerminalReleaseSuppressionStore(
            PathFor(),
            "Qobuzarr",
            ttl: ttl,
            clock: clock,
            refreshInterval: TimeSpan.Zero);

        await sut.SuppressAsync("album-expiring", "track-1", "Restricted");
        Assert.True(sut.IsSuppressed("album-expiring"));

        clock.Advance(ttl + TimeSpan.FromDays(1));

        Assert.False(sut.IsSuppressed("album-expiring"));
    }

    [Fact]
    public async Task SuppressAsync_NullOrWhitespaceReleaseId_IsNoOp()
    {
        var sut = new TerminalReleaseSuppressionStore(PathFor(), "Qobuzarr");

        await sut.SuppressAsync("", "track-1", "Restricted");
        await sut.SuppressAsync(null!, "track-1", "Restricted");

        Assert.False(sut.IsSuppressed(""));
        Assert.Equal(0, sut.Count);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset start) => _utcNow = start;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
