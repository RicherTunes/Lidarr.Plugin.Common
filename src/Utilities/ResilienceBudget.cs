using System;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// One monotonic elapsed-time owner for retry admission and redirect limits.
    /// Calendar time belongs to absolute Retry-After parsing, not duration budgets.
    /// </summary>
    internal readonly struct ResilienceBudget
    {
        private readonly TimeSpan _duration;
#if NET8_0_OR_GREATER
        private readonly TimeProvider _clock;
        private readonly long _startedAt;

        public ResilienceBudget(TimeSpan duration, TimeProvider? timeProvider = null)
        {
            _duration = duration;
            _clock = timeProvider ?? TimeProvider.System;
            _startedAt = _clock.GetTimestamp();
        }

        private TimeSpan Elapsed => _clock.GetElapsedTime(_startedAt);
#else
        private readonly System.Diagnostics.Stopwatch _stopwatch;

        public ResilienceBudget(TimeSpan duration)
        {
            _duration = duration;
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        }

        private TimeSpan Elapsed => _stopwatch.Elapsed;
#endif

        // Typed HTTP paths require strictly positive remaining time; direct callers
        // may supply zero/negative budgets and still receive their first response.
        public TimeSpan Remaining
        {
            get
            {
                var elapsed = Elapsed;
                return elapsed >= _duration ? TimeSpan.Zero : _duration - elapsed;
            }
        }

        // Once a response has been disposed to release its connection, budget
        // expiry is an explicit timeout, not permission to send another request.
        // Equality stays admitted: existing typed waits may clamp to the boundary.
        public void ThrowIfRetryExpired()
        {
            if (!CanFitDelay(TimeSpan.Zero))
            {
                throw new TimeoutException("The retry budget expired before the next attempt could start.");
            }
        }

        // Generic execution intentionally admits a zero delay at exact exhaustion.
        // Keep that contract distinct from typed HTTP's Remaining > 0 requirement.
        public bool CanFitDelay(TimeSpan delay)
        {
            var elapsed = Elapsed;
            return elapsed <= _duration && delay <= _duration - elapsed;
        }
    }
}
