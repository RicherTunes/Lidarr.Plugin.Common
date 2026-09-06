using System;
using System.Threading;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Owns the existing operation-wide timeout, including gate waits and retry delays.
/// The caller's cancellation remains distinguishable from deadline expiry.
/// </summary>
internal sealed class ResilienceTimeout : IDisposable
{
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource? _deadline;
    private readonly CancellationTokenSource? _linked;

    public ResilienceTimeout(TimeSpan? timeout, CancellationToken caller
#if NET8_0_OR_GREATER
        , TimeProvider? timeProvider = null
#endif
        )
    {
        _caller = caller;
        if (!timeout.HasValue || timeout.Value == Timeout.InfiniteTimeSpan)
        {
            Token = caller;
            return;
        }

#if NET8_0_OR_GREATER
        _deadline = new CancellationTokenSource(timeout.Value, timeProvider ?? TimeProvider.System);
#else
        _deadline = new CancellationTokenSource(timeout.Value);
#endif
        try
        {
            if (caller.CanBeCanceled)
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
            }
            Token = _linked?.Token ?? _deadline.Token;
        }
        catch
        {
            _deadline.Dispose();
            throw;
        }
    }

    public CancellationToken Token { get; }
    public bool IsTimeout => !_caller.IsCancellationRequested && _deadline?.IsCancellationRequested == true;

    public void Dispose()
    {
        _linked?.Dispose();
        _deadline?.Dispose();
    }
}
