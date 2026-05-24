using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="HostBridgeRuntimeCache{TRuntime, TSettings}"/>. The cache is
/// the generic version of apple's <c>AppleMusicLidarrRuntimeProvider</c> — handles the
/// "Lidarr instantiates indexer/dc directly, but we need a singleton runtime" pattern that
/// every plugin has independently re-derived. Pin the contract here so multiple plugins
/// can subclass without re-implementing the gate / graveyard / sweep / key-comparison logic.
///
/// Wave D item 6 from <c>memory/project_apple_bridge_unification_plan.md</c>.
/// </summary>
public class HostBridgeRuntimeCacheTests : IDisposable
{
    private sealed class FakeRuntime : IAsyncDisposable
    {
        public string AuthFingerprint { get; }
        public bool Disposed { get; private set; }
        public FakeRuntime(string auth) { AuthFingerprint = auth; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record FakeSettings(string AuthKey);

    private sealed class FakeCache : HostBridgeRuntimeCache<FakeRuntime, FakeSettings>
    {
        public int CreateCount;
        protected override string ComputeAuthKey(FakeSettings settings) => settings.AuthKey ?? "";
        protected override Task<FakeRuntime?> CreateAsync(FakeSettings settings, CancellationToken ct)
        {
            Interlocked.Increment(ref CreateCount);
            return Task.FromResult<FakeRuntime?>(new FakeRuntime(settings.AuthKey));
        }
    }

    private readonly FakeCache _cache = new();

    public void Dispose() => _cache.ResetAsync().GetAwaiter().GetResult();

    [Fact]
    public async Task GetAsync_SameKey_ReusesCachedRuntime()
    {
        var s = new FakeSettings("alice");
        var r1 = await _cache.GetAsync(s);
        var r2 = await _cache.GetAsync(s);
        Assert.NotNull(r1);
        Assert.Same(r1, r2);
        Assert.Equal(1, _cache.CreateCount);
    }

    [Fact]
    public async Task GetAsync_DifferentKey_BuildsNewRuntime()
    {
        var r1 = await _cache.GetAsync(new FakeSettings("alice"));
        var r2 = await _cache.GetAsync(new FakeSettings("bob"));
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.NotSame(r1, r2);
        Assert.Equal(2, _cache.CreateCount);
    }

    [Fact]
    public async Task GetAsync_KeyFlip_ParksPreviousRuntimeInGraveyard()
    {
        // Apple's PR #130 review #1 finding #4: when credentials change mid-flight, the
        // prior runtime must NOT be disposed eagerly — in-flight callers still hold it.
        // It goes to the graveyard and is disposed after the linger window.
        var alice = await _cache.GetAsync(new FakeSettings("alice"));
        var bob = await _cache.GetAsync(new FakeSettings("bob"));

        // alice's runtime is in the graveyard, NOT yet disposed.
        Assert.NotNull(alice);
        Assert.False(alice!.Disposed, "Eager disposal would have broken in-flight callers (PR #130 finding #4).");
        Assert.NotNull(bob);
    }

    [Fact]
    public async Task ResetAsync_DrainsGraveyardAndDisposesAllRuntimes()
    {
        var alice = await _cache.GetAsync(new FakeSettings("alice"));
        var bob = await _cache.GetAsync(new FakeSettings("bob"));
        var carol = await _cache.GetAsync(new FakeSettings("carol"));

        await _cache.ResetAsync();

        Assert.True(alice!.Disposed);
        Assert.True(bob!.Disposed);
        Assert.True(carol!.Disposed);
    }

    [Fact]
    public async Task GetAsync_AfterReset_BuildsFreshRuntime()
    {
        var alice1 = await _cache.GetAsync(new FakeSettings("alice"));
        await _cache.ResetAsync();
        var alice2 = await _cache.GetAsync(new FakeSettings("alice"));

        Assert.NotNull(alice1);
        Assert.NotNull(alice2);
        Assert.NotSame(alice1, alice2);
        Assert.True(alice1!.Disposed);
        Assert.False(alice2!.Disposed);
    }

    [Fact]
    public async Task GetAsync_NullCreateResult_PropagatesNull()
    {
        // ComputeAuthKey returning "" with a subclass that produces null is the "missing
        // credentials" path apple uses. The cache must NOT crash; it just returns null.
        var nullCache = new NullProducingCache();
        var result = await nullCache.GetAsync(new FakeSettings(""));
        Assert.Null(result);
    }

    private sealed class NullProducingCache : HostBridgeRuntimeCache<FakeRuntime, FakeSettings>
    {
        protected override string ComputeAuthKey(FakeSettings settings) => settings.AuthKey ?? "";
        protected override Task<FakeRuntime?> CreateAsync(FakeSettings settings, CancellationToken ct)
            => Task.FromResult<FakeRuntime?>(null);
    }
}
