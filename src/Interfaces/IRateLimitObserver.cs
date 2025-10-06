using System;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Observer notified when a server returns a Retry-After hint.
    /// </summary>
    public interface IRateLimitObserver
    {
        void RecordRetryAfter(TimeSpan delay, DateTimeOffset nowUtc);
    }
}

