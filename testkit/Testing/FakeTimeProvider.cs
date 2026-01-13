using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.TestKit.Testing;

/// <summary>
/// A fake implementation of <see cref="TimeProvider"/> for deterministic testing.
/// Allows tests to control time advancement without using Task.Delay or Thread.Sleep.
/// </summary>
/// <remarks>
/// <para><b>Usage:</b></para>
/// <code>
/// var fakeTime = new FakeTimeProvider();
/// var circuitBreaker = new CircuitBreaker("test", options, logger: null, timeProvider: fakeTime);
///
/// // Trigger circuit open
/// await Assert.ThrowsAsync&lt;Exception&gt;(() => circuitBreaker.ExecuteAsync(() => throw new Exception()));
///
/// // Advance time past open duration
/// fakeTime.Advance(TimeSpan.FromSeconds(31));
///
/// // Circuit should now be half-open
/// Assert.Equal(CircuitState.HalfOpen, circuitBreaker.State);
/// </code>
/// </remarks>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _utcNow;
    private readonly List<PendingDelay> _pendingDelays = new();

    /// <summary>
    /// Creates a new FakeTimeProvider starting at the specified time.
    /// </summary>
    /// <param name="startTime">Initial time. Defaults to 2024-01-01 00:00:00 UTC.</param>
    public FakeTimeProvider(DateTimeOffset? startTime = null)
    {
        _utcNow = startTime ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Gets the current fake UTC time.
    /// </summary>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _utcNow;
        }
    }

    /// <summary>
    /// Advances the fake time by the specified duration.
    /// Completes any pending delays whose due time has passed.
    /// </summary>
    /// <param name="delta">Duration to advance time by.</param>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "Cannot advance time backwards.");

        List<PendingDelay> completedDelays;

        lock (_lock)
        {
            _utcNow += delta;

            // Find all delays that should complete
            completedDelays = _pendingDelays
                .Where(d => d.DueTime <= _utcNow)
                .ToList();

            foreach (var delay in completedDelays)
            {
                _pendingDelays.Remove(delay);
            }
        }

        // Complete outside the lock to avoid deadlocks
        foreach (var delay in completedDelays)
        {
            delay.Complete();
        }
    }

    /// <summary>
    /// Sets the fake time to a specific value.
    /// Completes any pending delays whose due time has passed.
    /// </summary>
    /// <param name="time">The new fake time.</param>
    public void SetUtcNow(DateTimeOffset time)
    {
        List<PendingDelay> completedDelays;

        lock (_lock)
        {
            if (time < _utcNow)
                throw new ArgumentOutOfRangeException(nameof(time), "Cannot set time to a past value.");

            _utcNow = time;

            // Find all delays that should complete
            completedDelays = _pendingDelays
                .Where(d => d.DueTime <= _utcNow)
                .ToList();

            foreach (var delay in completedDelays)
            {
                _pendingDelays.Remove(delay);
            }
        }

        // Complete outside the lock to avoid deadlocks
        foreach (var delay in completedDelays)
        {
            delay.Complete();
        }
    }

    /// <summary>
    /// Creates a delay that completes when fake time is advanced past the due time.
    /// </summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // For circuit breaker tests, we typically don't need periodic timers
        // This is a simplified implementation that fires once
        var timer = new FakeTimer(this, callback, state, dueTime);
        return timer;
    }

    /// <summary>
    /// Creates a task that completes when fake time is advanced past the delay duration.
    /// </summary>
    /// <param name="delay">The delay duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when time is advanced.</returns>
    public Task CreateDelayTask(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            var dueTime = _utcNow + delay;

            // If already past due, complete immediately
            if (dueTime <= _utcNow)
            {
                return Task.CompletedTask;
            }

            var pendingDelay = new PendingDelay(dueTime, tcs, cancellationToken);
            _pendingDelays.Add(pendingDelay);

            // Handle cancellation
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        _pendingDelays.Remove(pendingDelay);
                    }
                    tcs.TrySetCanceled(cancellationToken);
                });
            }
        }

        return tcs.Task;
    }

    /// <summary>
    /// Gets the number of pending delays waiting for time advancement.
    /// </summary>
    public int PendingDelayCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingDelays.Count;
            }
        }
    }

    private sealed class PendingDelay
    {
        public DateTimeOffset DueTime { get; }
        private readonly TaskCompletionSource _tcs;
        private readonly CancellationToken _cancellationToken;

        public PendingDelay(DateTimeOffset dueTime, TaskCompletionSource tcs, CancellationToken cancellationToken)
        {
            DueTime = dueTime;
            _tcs = tcs;
            _cancellationToken = cancellationToken;
        }

        public void Complete()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(_cancellationToken);
            }
            else
            {
                _tcs.TrySetResult();
            }
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _provider = provider;
            _callback = callback;
            _state = state;

            if (dueTime != Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero)
            {
                // Schedule the callback when time advances
                _ = ScheduleCallbackAsync(dueTime);
            }
        }

        private async Task ScheduleCallbackAsync(TimeSpan dueTime)
        {
            await _provider.CreateDelayTask(dueTime);
            if (!_disposed)
            {
                _callback(_state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // Simplified - not fully implementing timer changes
            return !_disposed;
        }

        public void Dispose()
        {
            _disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
