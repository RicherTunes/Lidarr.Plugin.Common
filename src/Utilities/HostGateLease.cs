using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Owns permits and their registry reservations. Pending acquisitions remain
/// protected from retirement; permit release always precedes reservation release.
/// </summary>
internal sealed class HostGateLease : IDisposable
{
    private readonly SemaphoreSlim? _aggregate;
    private readonly SemaphoreSlim _profile;
    private readonly IDisposable? _reservation;
    private int _released;

    private HostGateLease(SemaphoreSlim? aggregate, SemaphoreSlim profile, IDisposable? reservation)
    {
        _aggregate = aggregate;
        _profile = profile;
        _reservation = reservation;
    }

    public static Task<HostGateLease> AcquireAsync(string? host, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = HostGateRegistry.Reserve(host, limit);
        return AcquireCoreAsync(null, reservation.Profile, reservation, cancellationToken);
    }

    public static Task<HostGateLease> AcquireAsync(string? host, string? profileKey, int aggregateLimit, int profileLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = HostGateRegistry.Reserve(profileKey, profileLimit, host, aggregateLimit);
        return AcquireCoreAsync(reservation.Aggregate, reservation.Profile, reservation, cancellationToken);
    }

    // For externally owned semaphores (including focused permit-ownership tests).
    // Registry consumers must use the key-based overloads above.
    public static Task<HostGateLease> AcquireAsync(SemaphoreSlim aggregate, SemaphoreSlim profile, CancellationToken cancellationToken)
        => AcquireCoreAsync(aggregate, profile, null, cancellationToken);

    private static async Task<HostGateLease> AcquireCoreAsync(SemaphoreSlim? aggregate, SemaphoreSlim profile,
        IDisposable? reservation, CancellationToken cancellationToken)
    {
        var aggregateHeld = false;
        var profileHeld = false;
        try
        {
            if (aggregate is not null)
            {
                await aggregate.WaitAsync(cancellationToken).ConfigureAwait(false);
                aggregateHeld = true;
            }
            await profile.WaitAsync(cancellationToken).ConfigureAwait(false);
            profileHeld = true;
            return new HostGateLease(aggregate, profile, reservation);
        }
        catch
        {
            try
            {
                if (profileHeld) profile.Release();
            }
            finally
            {
                try { if (aggregateHeld) aggregate!.Release(); }
                finally { reservation?.Dispose(); }
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try { _profile.Release(); }
        finally
        {
            try { _aggregate?.Release(); }
            finally { _reservation?.Dispose(); }
        }
    }
}
