using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Performance;

/// <summary>
/// Abstract base for service-specific thin wrappers around
/// <see cref="UniversalAdaptiveRateLimiter"/>. Encapsulates the dispose-guard and
/// delegation boilerplate shared by Tidal, Qobuz, and (optionally) Apple Music
/// plugin-local rate-limiter adapters.
///
/// <para>
/// Each plugin subclass typically only overrides what is unique: service-name
/// normalization, stat-helper shortcuts, or (as in Apple's conservative decorator)
/// per-endpoint budget seeding logic.
/// </para>
///
/// <para>Lifted as Wave-57 from:
/// <c>Tidalarr/Infrastructure/Performance/TidalRateLimiter.cs</c>,
/// <c>Qobuzarr/Services/Performance/AdaptiveRateLimiter.cs</c>, and
/// <c>AppleMusicarr/Runtime/ConservativeAppleMusicRateLimiter+ConservativeDecorator</c>.
/// </para>
/// </summary>
public abstract class NamedServiceRateLimiter : IUniversalAdaptiveRateLimiter
{
    /// <summary>Underlying shared rate-limiter instance.</summary>
    protected UniversalAdaptiveRateLimiter Inner { get; }

    /// <summary>Canonical service name passed to <see cref="Inner"/> for all calls.</summary>
    protected string ServiceName { get; }

    private bool _disposed;

    /// <summary>
    /// Initialises the limiter.
    /// </summary>
    /// <param name="serviceName">
    ///   The canonical service name (e.g., <c>"Tidal"</c>, <c>"Qobuz"</c>,
    ///   <c>"AppleMusic"</c>) used as the default <c>service</c> argument for
    ///   every delegation call.
    /// </param>
    /// <param name="inner">
    ///   An existing <see cref="UniversalAdaptiveRateLimiter"/> to wrap. When
    ///   <c>null</c> a fresh instance is constructed.
    /// </param>
    protected NamedServiceRateLimiter(string serviceName, UniversalAdaptiveRateLimiter? inner = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name must not be empty.", nameof(serviceName));

        ServiceName = serviceName;
        Inner = inner ?? new UniversalAdaptiveRateLimiter();
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if this instance has been disposed.
    /// Call at the top of every method that must not proceed after disposal.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    // -------------------------------------------------------------------------
    // IUniversalAdaptiveRateLimiter — default delegation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public virtual Task<bool> WaitIfNeededAsync(
        string service,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Inner.WaitIfNeededAsync(
            NormalizeService(service), endpoint ?? string.Empty, cancellationToken);
    }

    /// <inheritdoc/>
    public virtual void RecordResponse(
        string service,
        string endpoint,
        HttpResponseMessage response)
    {
        if (_disposed)
            return;

        Inner.RecordResponse(NormalizeService(service), endpoint ?? string.Empty, response);
    }

    /// <inheritdoc/>
    public virtual void RecordAuthFailure(string service, string endpoint)
    {
        if (_disposed)
            return;

        Inner.RecordAuthFailure(NormalizeService(service), endpoint ?? string.Empty);
    }

    /// <inheritdoc/>
    public virtual int GetCurrentLimit(string service, string endpoint)
    {
        ThrowIfDisposed();
        return Inner.GetCurrentLimit(NormalizeService(service), endpoint ?? string.Empty);
    }

    /// <inheritdoc/>
    public virtual ServiceRateLimitStats GetServiceStats(string service)
    {
        ThrowIfDisposed();
        return Inner.GetServiceStats(NormalizeService(service));
    }

    /// <inheritdoc/>
    public virtual GlobalRateLimitStats GetGlobalStats()
    {
        ThrowIfDisposed();
        return Inner.GetGlobalStats();
    }

    // -------------------------------------------------------------------------
    // Shortcut helpers (pre-filled service name)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Waits if needed, automatically using <see cref="ServiceName"/> as the service tag.
    /// </summary>
    public Task<bool> WaitIfNeededAsync(string endpoint, CancellationToken cancellationToken = default)
        => WaitIfNeededAsync(ServiceName, endpoint, cancellationToken);

    /// <summary>
    /// Records a response, automatically using <see cref="ServiceName"/> as the service tag.
    /// </summary>
    public void RecordResponse(string endpoint, HttpResponseMessage response)
        => RecordResponse(ServiceName, endpoint, response);

    /// <summary>
    /// Returns stats for <see cref="ServiceName"/>.
    /// </summary>
    public ServiceRateLimitStats GetNamedServiceStats()
        => GetServiceStats(ServiceName);

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Inner.Dispose();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see cref="ServiceName"/> when <paramref name="service"/> is null or
    /// whitespace, otherwise returns the caller-supplied value unchanged.
    /// </summary>
    private string NormalizeService(string service)
        => string.IsNullOrWhiteSpace(service) ? ServiceName : service;
}
