using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Acquires the aggregate and profile gates as a pair. Partial acquisition rolls
/// back, and the resulting lease releases each owned permit exactly once.
/// </summary>
internal sealed class HostGateLease : IDisposable
{
    private readonly SemaphoreSlim _aggregate;
    private readonly SemaphoreSlim _profile;
    private int _released;

    private HostGateLease(SemaphoreSlim aggregate, SemaphoreSlim profile)
    {
        _aggregate = aggregate;
        _profile = profile;
    }

    public static async Task<HostGateLease> AcquireAsync(SemaphoreSlim aggregate, SemaphoreSlim profile, CancellationToken cancellationToken)
    {
        await aggregate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await profile.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            aggregate.Release();
            throw;
        }
        return new HostGateLease(aggregate, profile);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }
        try { _profile.Release(); }
        finally { _aggregate.Release(); }
    }
}
