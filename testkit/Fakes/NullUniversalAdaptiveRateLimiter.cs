using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;

namespace Lidarr.Plugin.Common.TestKit.Fakes;

/// <summary>
/// No-op implementation of <see cref="IUniversalAdaptiveRateLimiter"/> for tests that don't
/// care about rate-limiting behavior but still need the dependency satisfied.
///
/// <para><b>Why this lives in TestKit rather than each plugin re-implementing it:</b> every
/// time Common adds a member to <see cref="IUniversalAdaptiveRateLimiter"/> (e.g.
/// <c>RecordAuthFailure</c> added during the auth-failure-gate work), every plugin's
/// hand-rolled "NoopLimiter" test fixture stops compiling. The plugins notice in their next
/// test run — but until then, the build break sits latent. Centralizing the null object
/// here means: bump the Common submodule → plugin tests still compile (or break with a clear
/// "I added a member to the interface, update NullUniversalAdaptiveRateLimiter" message in
/// one place).</para>
///
/// <para><b>When to use:</b> any test that constructs a class which depends on
/// <c>IUniversalAdaptiveRateLimiter</c> but only exercises non-rate-limiting code paths.
/// When you actually want to verify rate-limiting behavior, use the real
/// <see cref="UniversalAdaptiveRateLimiter"/> with a fast configuration.</para>
/// </summary>
public sealed class NullUniversalAdaptiveRateLimiter : IUniversalAdaptiveRateLimiter
{
    /// <summary>Singleton instance — the null object holds no state.</summary>
    public static readonly NullUniversalAdaptiveRateLimiter Instance = new();

    private NullUniversalAdaptiveRateLimiter() { }

    public Task<bool> WaitIfNeededAsync(string service, string endpoint, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void RecordResponse(string service, string endpoint, HttpResponseMessage response)
    {
        // No-op: null object discards observations.
    }

    /// <summary>
    /// Explicitly implemented so callers holding a concrete <c>NullUniversalAdaptiveRateLimiter</c>
    /// reference can invoke this method — relying on the interface's default implementation
    /// would only work for callers holding an <c>IUniversalAdaptiveRateLimiter</c>-typed
    /// reference. Tests written against the fake's concrete type benefit from the explicit method.
    /// </summary>
    public void RecordAuthFailure(string service, string endpoint)
    {
        // No-op: null object discards observations.
    }

    public int GetCurrentLimit(string service, string endpoint) => int.MaxValue;

    public ServiceRateLimitStats GetServiceStats(string service) => new();

    public GlobalRateLimitStats GetGlobalStats() => new();

    public void Dispose()
    {
        // No-op: no resources to release.
    }
}
