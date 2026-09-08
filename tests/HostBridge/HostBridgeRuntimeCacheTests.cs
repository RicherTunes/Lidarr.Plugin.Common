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
    private sealed class ControlledRuntime : IAsyncDisposable
    {
        private readonly bool _blocks;
        private readonly bool _throws;
        private int _disposeCalls;

        public ControlledRuntime(bool blocks, bool throws)
        {
            _blocks = blocks;
            _throws = throws;
        }

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeStarted.TrySetResult();
            try
            {
                if (_blocks)
                {
                    await ReleaseDispose.Task.ConfigureAwait(false);
                }
                if (_throws)
                {
                    throw new InvalidOperationException("Controlled disposal failure");
                }
            }
            finally
            {
                DisposeFinished.TrySetResult();
            }
        }
    }

    private sealed record ControlledSettings(string AuthKey, bool BlocksDispose = false, bool ThrowsOnDispose = false);

    private sealed class ControlledCache : HostBridgeRuntimeCache<ControlledRuntime, ControlledSettings>
    {
        public System.Collections.Generic.Dictionary<string, ControlledRuntime> Runtimes { get; } = new();
        public int CreateCount { get; private set; }

        protected override int GraveyardMaxSize => 1;
        protected override int GraveyardLingerSeconds => int.MaxValue;
        protected override string ComputeAuthKey(ControlledSettings settings) => settings.AuthKey;

        protected override Task<ControlledRuntime?> CreateAsync(ControlledSettings settings, CancellationToken cancellationToken)
        {
            CreateCount++;
            var runtime = new ControlledRuntime(settings.BlocksDispose, settings.ThrowsOnDispose);
            Runtimes.Add(settings.AuthKey, runtime);
            return Task.FromResult<ControlledRuntime?>(runtime);
        }
    }

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
    public async Task ResetAsync_WaitsForPreviouslyDispatchedOverflowDisposal()
    {
        var cache = new ControlledCache();
        Task? resetTask = null;
        ControlledRuntime? runtimeA = null;
        ControlledRuntime? runtimeB = null;
        ControlledRuntime? runtimeC = null;

        try
        {
            runtimeA = await cache.GetAsync(new ControlledSettings("A", BlocksDispose: true));
            await cache.GetAsync(new ControlledSettings("B"));
            await cache.GetAsync(new ControlledSettings("C"));
            runtimeB = cache.Runtimes["B"];
            runtimeC = cache.Runtimes["C"];

            await runtimeA!.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, runtimeA.DisposeCalls);
            Assert.False(runtimeA.DisposeFinished.Task.IsCompleted);

            resetTask = cache.ResetAsync();

            Assert.False(resetTask.IsCompleted, "Reset must retain ownership of an overflow disposal already dispatched in the background.");

            runtimeA.ReleaseDispose.TrySetResult();
            await runtimeA.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await resetTask.WaitAsync(TimeSpan.FromSeconds(10));
            await runtimeB!.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtimeC!.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, runtimeA.DisposeCalls);
            Assert.Equal(1, runtimeB.DisposeCalls);
            Assert.Equal(1, runtimeC.DisposeCalls);
        }
        finally
        {
            runtimeA?.ReleaseDispose.TrySetResult();
            try
            {
                if (resetTask is not null)
                {
                    await resetTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await cache.ResetAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                if (runtimeA is not null && runtimeA.DisposeStarted.Task.IsCompleted)
                {
                    await runtimeA.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
    }

    [Fact]
    public async Task GetAsync_OverlappingReset_WaitsForResetGeneration()
    {
        var (cache, runtimeA) = await CreateBlockedOverflowAsync();
        Task? resetTask = null;
        Task<ControlledRuntime?>? getTask = null;

        try
        {
            resetTask = cache.ResetAsync();
            getTask = cache.GetAsync(new ControlledSettings("D"));

            Assert.False(getTask.IsCompleted, "A lookup overlapping reset must not repopulate the cache before that reset generation finishes.");
            Assert.Equal(3, cache.CreateCount);

            runtimeA.ReleaseDispose.TrySetResult();
            await resetTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(await getTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(4, cache.CreateCount);
        }
        finally
        {
            runtimeA.ReleaseDispose.TrySetResult();
            await AwaitCleanupAsync(cache, runtimeA, resetTask, getTask);
        }
    }

    [Fact]
    public async Task GetAsync_CancelledResetWaiter_DoesNotCancelSharedReset()
    {
        var (cache, runtimeA) = await CreateBlockedOverflowAsync();
        Task? resetTask = null;
        Task<ControlledRuntime?>? getTask = null;
        using var cancellation = new CancellationTokenSource();

        try
        {
            resetTask = cache.ResetAsync();
            getTask = cache.GetAsync(new ControlledSettings("D"), cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await getTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(resetTask.IsCompleted, "Cancelling one lookup waiter must not cancel the shared reset generation.");

            runtimeA.ReleaseDispose.TrySetResult();
            await resetTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            runtimeA.ReleaseDispose.TrySetResult();
            await AwaitCleanupAsync(cache, runtimeA, resetTask, getTask);
        }
    }

    [Fact]
    public async Task ResetAsync_OverlappingReset_CoalescesGenerationAndDisposesOnce()
    {
        var (cache, runtimeA) = await CreateBlockedOverflowAsync();
        var runtimeB = cache.Runtimes["B"];
        var runtimeC = cache.Runtimes["C"];
        Task? firstReset = null;
        Task? secondReset = null;

        try
        {
            firstReset = cache.ResetAsync();
            secondReset = cache.ResetAsync();

            Assert.False(secondReset.IsCompleted, "An overlapping reset must wait for the active reset generation.");

            runtimeA.ReleaseDispose.TrySetResult();
            await Task.WhenAll(firstReset, secondReset).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, runtimeA.DisposeCalls);
            Assert.Equal(1, runtimeB.DisposeCalls);
            Assert.Equal(1, runtimeC.DisposeCalls);
        }
        finally
        {
            runtimeA.ReleaseDispose.TrySetResult();
            await AwaitCleanupAsync(cache, runtimeA, firstReset, secondReset);
        }
    }

    [Fact]
    public async Task ResetAsync_SynchronousAndThrowingDisposers_ContinueDrainAndReleaseGeneration()
    {
        var cache = new ControlledCache();
        await cache.GetAsync(new ControlledSettings("A"));
        await cache.GetAsync(new ControlledSettings("B", ThrowsOnDispose: true));
        await cache.GetAsync(new ControlledSettings("C"));
        var runtimeA = cache.Runtimes["A"];
        var runtimeB = cache.Runtimes["B"];
        var runtimeC = cache.Runtimes["C"];

        await cache.ResetAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await runtimeA.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtimeB.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtimeC.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, runtimeA.DisposeCalls);
        Assert.Equal(1, runtimeB.DisposeCalls);
        Assert.Equal(1, runtimeC.DisposeCalls);

        Assert.NotNull(await cache.GetAsync(new ControlledSettings("D")));
        await cache.ResetAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<(ControlledCache Cache, ControlledRuntime RuntimeA)> CreateBlockedOverflowAsync()
    {
        var cache = new ControlledCache();
        ControlledRuntime? runtimeA = null;
        try
        {
            runtimeA = await cache.GetAsync(new ControlledSettings("A", BlocksDispose: true));
            await cache.GetAsync(new ControlledSettings("B"));
            await cache.GetAsync(new ControlledSettings("C"));
            await runtimeA!.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(runtimeA.DisposeFinished.Task.IsCompleted);
            return (cache, runtimeA);
        }
        catch
        {
            runtimeA?.ReleaseDispose.TrySetResult();
            await cache.ResetAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (runtimeA is not null && runtimeA.DisposeStarted.Task.IsCompleted)
            {
                await runtimeA.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            throw;
        }
    }

    private static async Task AwaitCleanupAsync(ControlledCache cache, ControlledRuntime runtimeA, params Task?[] tasks)
    {
        runtimeA.ReleaseDispose.TrySetResult();
        try
        {
            foreach (var task in tasks)
            {
                if (task is not null)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch
                    {
                        // Cleanup must continue after a failed or cancelled task.
                    }
                }
            }
            await cache.ResetAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (runtimeA.DisposeStarted.Task.IsCompleted)
            {
                await runtimeA.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
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
