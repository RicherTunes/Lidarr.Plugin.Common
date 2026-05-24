using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Generic singleton-runtime cache for Lidarr-native bridge plugins. Solves the
/// "host instantiates indexer/dc directly via reflection, but we need a process-wide
/// runtime keyed on auth-critical settings" pattern that every plugin has independently
/// re-derived (apple's <c>AppleMusicLidarrRuntimeProvider</c>, tidal's
/// <c>EnsureServicesInitialized</c>, etc.).
///
/// <para>What the cache provides:</para>
/// <list type="bullet">
///   <item><b>Gated initialization</b> — a <see cref="SemaphoreSlim"/> ensures concurrent
///         <c>GetAsync</c> callers see one runtime, not N parallel constructions.</item>
///   <item><b>Auth-key invalidation</b> — subclass returns an opaque key string from auth
///         settings; when the key changes, a new runtime is built.</item>
///   <item><b>Deferred-disposal graveyard</b> — previous runtime is parked for
///         <see cref="GraveyardLingerSeconds"/> seconds before its <c>DisposeAsync</c>
///         runs, so in-flight callers holding the prior runtime via captured locals
///         don't crash with <c>ObjectDisposedException</c> (apple PR #130 review #1
///         finding #4).</item>
///   <item><b>Bounded graveyard</b> — under credential-thrash, the oldest entries are
///         force-disposed to keep the queue bounded (apple PR #130 review #2 finding #4).</item>
///   <item><b>Fire-and-forget disposal</b> — <c>DisposeAsync</c> runs on a
///         background <see cref="Task"/> so request threads don't block on HttpClient
///         shutdown.</item>
///   <item><b>Test-only reset</b> — <see cref="ResetAsync"/> drains both the cache and
///         the graveyard.</item>
/// </list>
///
/// <para>Subclass contract:</para>
/// <code>
/// public sealed class MyRuntimeCache : HostBridgeRuntimeCache&lt;MyRuntime, MySettings&gt;
/// {
///     protected override string ComputeAuthKey(MySettings settings)
///         =&gt; SHA256(settings.ApiKey + "|" + settings.UserToken);
///
///     protected override Task&lt;MyRuntime?&gt; CreateAsync(MySettings settings, CancellationToken ct)
///         =&gt; MyRuntime.CreateAsync(settings.ApiKey, settings.UserToken, ct);
/// }
/// </code>
///
/// <para>Lifted as Wave D item 6 from
/// <c>memory/project_apple_bridge_unification_plan.md</c>.</para>
/// </summary>
/// <typeparam name="TRuntime">The plugin's runtime/service-collection root type. Must
/// implement <see cref="IAsyncDisposable"/> so the graveyard can dispose it cleanly.</typeparam>
/// <typeparam name="TSettings">The plugin's host-shape settings (whichever type carries
/// the auth-critical fields).</typeparam>
public abstract class HostBridgeRuntimeCache<TRuntime, TSettings>
    where TRuntime : class, IAsyncDisposable
    where TSettings : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TRuntime? _cachedRuntime;
    private string? _cachedKey;

    private readonly ConcurrentQueue<(DateTime ParkedAt, TRuntime Runtime)> _graveyard = new();

    /// <summary>Linger window before a retired runtime is disposed. Default 60s.</summary>
    protected virtual int GraveyardLingerSeconds => 60;

    /// <summary>Hard cap on graveyard size. Force-dispose oldest entry on overflow.</summary>
    protected virtual int GraveyardMaxSize => 16;

    /// <summary>
    /// Compute the auth-fingerprint key. Used for cache-hit comparison. Should hash the
    /// auth-critical fields ONLY (changing a non-critical field shouldn't invalidate the
    /// cached runtime). Return value is treated as opaque — typically SHA256-hex.
    /// </summary>
    protected abstract string ComputeAuthKey(TSettings settings);

    /// <summary>
    /// Build a fresh runtime from settings. Return null to signal "settings incomplete /
    /// missing credentials" — the cache will propagate the null up to the caller without
    /// storing it.
    /// </summary>
    protected abstract Task<TRuntime?> CreateAsync(TSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Return a runtime for the given settings — cached if auth fields haven't changed,
    /// freshly constructed (with the prior runtime parked for deferred disposal) otherwise.
    /// Returns null if <see cref="CreateAsync"/> returns null.
    /// </summary>
    public async Task<TRuntime?> GetAsync(TSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        // Sweep ripe graveyard entries outside the gate so a slow Dispose doesn't block
        // fresh-credential lookups.
        SweepGraveyard();

        var key = ComputeAuthKey(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedRuntime is not null && string.Equals(key, _cachedKey, StringComparison.Ordinal))
            {
                return _cachedRuntime;
            }

            // Park the prior runtime in the graveyard rather than disposing eagerly.
            if (_cachedRuntime is not null)
            {
                EnqueueWithBound(_cachedRuntime);
                _cachedRuntime = null;
                _cachedKey = null;
            }

            var built = await CreateAsync(settings, cancellationToken).ConfigureAwait(false);
            if (built is null)
            {
                return null;
            }

            _cachedRuntime = built;
            _cachedKey = key;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Test-only reset: dispose the current runtime AND drain the graveyard. Drops both
    /// the cache slot and any parked runtimes so the next test starts clean.
    /// </summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedRuntime is not null)
            {
                try { await _cachedRuntime.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
                _cachedRuntime = null;
                _cachedKey = null;
            }

            while (_graveyard.TryDequeue(out var parked))
            {
                try { await parked.Runtime.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnqueueWithBound(TRuntime runtime)
    {
        while (_graveyard.Count >= GraveyardMaxSize && _graveyard.TryDequeue(out var oldest))
        {
            FireAndForgetDispose(oldest.Runtime);
        }
        _graveyard.Enqueue((DateTime.UtcNow, runtime));
    }

    private void SweepGraveyard()
    {
        var lingerSeconds = GraveyardLingerSeconds;
        var now = DateTime.UtcNow;
        while (_graveyard.TryPeek(out var parked))
        {
            if ((now - parked.ParkedAt).TotalSeconds < lingerSeconds)
            {
                return;
            }
            if (!_graveyard.TryDequeue(out var head))
            {
                return;
            }
            FireAndForgetDispose(head.Runtime);
        }
    }

    private static void FireAndForgetDispose(TRuntime runtime)
        => _ = Task.Run(async () =>
        {
            try { await runtime.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        });
}
