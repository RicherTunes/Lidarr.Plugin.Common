// <copyright file="WarnOnceTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

using Lidarr.Plugin.Common.Diagnostics;

namespace Lidarr.Plugin.Common.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="WarnOnce"/>.
/// </summary>
public class WarnOnceTests
{
    // -----------------------------------------------------------------------
    // First-call behaviour
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWarn_FirstCall_ReturnsTrueAndInvokesWarnAction()
    {
        var sut = new WarnOnce();
        var warnCount = 0;

        var result = sut.TryWarn("key-a", () => warnCount++);

        Assert.True(result);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void TryWarn_FirstCallWithDebugAction_ReturnsTrueAndDoesNotInvokeDebug()
    {
        var sut = new WarnOnce();
        var warnCount = 0;
        var debugCount = 0;

        var result = sut.TryWarn("key-a", () => warnCount++, () => debugCount++);

        Assert.True(result);
        Assert.Equal(1, warnCount);
        Assert.Equal(0, debugCount);
    }

    // -----------------------------------------------------------------------
    // Subsequent-call behaviour
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWarn_SubsequentCall_ReturnsFalseAndInvokesDebugAction()
    {
        var sut = new WarnOnce();
        var warnCount = 0;
        var debugCount = 0;

        sut.TryWarn("key-b", () => warnCount++, () => debugCount++);
        var result = sut.TryWarn("key-b", () => warnCount++, () => debugCount++);

        Assert.False(result);
        Assert.Equal(1, warnCount);
        Assert.Equal(1, debugCount);
    }

    [Fact]
    public void TryWarn_SubsequentCallWithNoDebugAction_ReturnsFalseAndIsSilent()
    {
        var sut = new WarnOnce();
        var warnCount = 0;

        sut.TryWarn("key-c", () => warnCount++);
        var result = sut.TryWarn("key-c", () => warnCount++);

        Assert.False(result);
        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void TryWarn_ManySubsequentCalls_WarnInvokedExactlyOnce()
    {
        var sut = new WarnOnce();
        var warnCount = 0;
        var debugCount = 0;

        for (var i = 0; i < 10; i++)
        {
            sut.TryWarn("key-d", () => warnCount++, () => debugCount++);
        }

        Assert.Equal(1, warnCount);
        Assert.Equal(9, debugCount);
    }

    // -----------------------------------------------------------------------
    // Key independence
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWarn_DifferentKeys_AreIndependent()
    {
        var sut = new WarnOnce();
        var counts = new Dictionary<string, int>();

        foreach (var key in new[] { "key-x", "key-y", "key-z" })
        {
            counts[key] = 0;
            sut.TryWarn(key, () => counts[key]++);
            sut.TryWarn(key, () => counts[key]++);
        }

        // Each key: first call fires warn, second does not → count == 1
        Assert.Equal(1, counts["key-x"]);
        Assert.Equal(1, counts["key-y"]);
        Assert.Equal(1, counts["key-z"]);
    }

    [Fact]
    public void TryWarn_CompositeKeys_TreatedAsSeparate()
    {
        var sut = new WarnOnce();
        var warnCount = 0;

        // Mimics Brainarr's $"{reason}:{normalizedModelKey}" pattern
        sut.TryWarn("provider-prefix:claude:claude-3-opus", () => warnCount++);
        sut.TryWarn("default-fallback:gpt-4o", () => warnCount++);

        Assert.Equal(2, warnCount);
    }

    // -----------------------------------------------------------------------
    // Exception overload
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWarn_ExceptionOverload_FirstCallPassesExToWarnAction()
    {
        var sut = new WarnOnce();
        var ex = new InvalidOperationException("boom");
        Exception? received = null;

        var result = sut.TryWarn("key-ex", ex, e => received = e);

        Assert.True(result);
        Assert.Same(ex, received);
    }

    [Fact]
    public void TryWarn_ExceptionOverload_SubsequentCallPassesExToDebugAction()
    {
        var sut = new WarnOnce();
        var ex = new InvalidOperationException("boom");
        var warnReceived = new List<Exception>();
        var debugReceived = new List<Exception>();

        sut.TryWarn("key-ex2", ex, e => warnReceived.Add(e), e => debugReceived.Add(e));
        var result = sut.TryWarn("key-ex2", ex, e => warnReceived.Add(e), e => debugReceived.Add(e));

        Assert.False(result);
        Assert.Single(warnReceived);
        Assert.Single(debugReceived);
    }

    [Fact]
    public void TryWarn_ExceptionOverload_NoDebugAction_SubsequentCallIsSilent()
    {
        var sut = new WarnOnce();
        var ex = new InvalidOperationException("boom");
        var warnCount = 0;

        sut.TryWarn("key-ex3", ex, _ => warnCount++);
        var result = sut.TryWarn("key-ex3", ex, _ => warnCount++);

        Assert.False(result);
        Assert.Equal(1, warnCount);
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    [Fact]
    public void Reset_ClearsSeenKeys_AllowsWarnToFireAgain()
    {
        var sut = new WarnOnce();
        var warnCount = 0;

        sut.TryWarn("key-r", () => warnCount++);
        sut.Reset();
        sut.TryWarn("key-r", () => warnCount++);

        Assert.Equal(2, warnCount);
    }

    [Fact]
    public void Reset_MultipleKeys_AllCleared()
    {
        var sut = new WarnOnce();
        var warnCount = 0;

        foreach (var k in new[] { "a", "b", "c" })
        {
            sut.TryWarn(k, () => warnCount++);
        }

        Assert.Equal(3, warnCount);

        sut.Reset();

        foreach (var k in new[] { "a", "b", "c" })
        {
            sut.TryWarn(k, () => warnCount++);
        }

        Assert.Equal(6, warnCount);
    }

    // -----------------------------------------------------------------------
    // Concurrency: exactly one warn fires per key
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryWarn_ConcurrentCalls_ExactlyOneWarnFires()
    {
        // 16 threads is enough to exercise the TryAdd race condition we care about;
        // the prior 64-thread version exhausted the thread pool when the full test
        // suite ran in parallel (xunit MaxParallelThreads default = CPU count), so
        // Barrier(64).SignalAndWait could hang waiting for threads that never get
        // scheduled. The contract under test is "exactly one warn fires per key
        // when N threads race"; that's verified at any N >= 2.
        const int threadCount = 16;
        var sut = new WarnOnce();
        var warnCount = 0;
        var debugCount = 0;
        var barrier = new Barrier(threadCount);

        var tasks = new Task[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30)); // fail-fast if pool starves
                sut.TryWarn(
                    "concurrent-key",
                    () => Interlocked.Increment(ref warnCount),
                    () => Interlocked.Increment(ref debugCount));
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, warnCount);
        Assert.Equal(threadCount - 1, debugCount);
    }

    [Fact]
    public async Task TryWarn_ConcurrentCallsDifferentKeys_EachKeyWarnsFiredExactlyOnce()
    {
        // Reduced from 16 keys × 8 threads (=128) to 8×4 (=32) for the same reason
        // as TryWarn_ConcurrentCalls above — full-suite parallelism + Barrier(N) hangs
        // when N exceeds the available thread-pool worker count. 32 still exercises
        // the per-key independence property (each key gets its own race).
        const int keyCount = 8;
        const int threadsPerKey = 4;
        var sut = new WarnOnce();
        var warnCounts = new int[keyCount];
        var barrier = new Barrier(keyCount * threadsPerKey);

        var tasks = new List<Task>();
        for (var k = 0; k < keyCount; k++)
        {
            var capturedK = k;
            for (var t = 0; t < threadsPerKey; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                    sut.TryWarn(
                        $"key-{capturedK}",
                        () => Interlocked.Increment(ref warnCounts[capturedK]));
                }));
            }
        }

        await Task.WhenAll(tasks);

        for (var k = 0; k < keyCount; k++)
        {
            Assert.Equal(1, warnCounts[k]);
        }
    }

    // -----------------------------------------------------------------------
    // Constructor variants
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_WithOrdinalIgnoreCaseComparer_TreatsKeysAsCaseInsensitive()
    {
        var sut = new WarnOnce(StringComparer.OrdinalIgnoreCase);
        var warnCount = 0;

        sut.TryWarn("MyKey", () => warnCount++);
        sut.TryWarn("mykey", () => warnCount++); // same key, different case

        Assert.Equal(1, warnCount);
    }

    [Fact]
    public void Constructor_NullComparer_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new WarnOnce(null!));
    }

    // -----------------------------------------------------------------------
    // Guard-rail: invalid arguments
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWarn_NullKey_ThrowsArgumentNullException()
    {
        var sut = new WarnOnce();
        Assert.Throws<ArgumentNullException>(() => sut.TryWarn(null!, () => { }));
    }

    [Fact]
    public void TryWarn_EmptyKey_ThrowsArgumentException()
    {
        var sut = new WarnOnce();
        Assert.Throws<ArgumentException>(() => sut.TryWarn("", () => { }));
    }

    [Fact]
    public void TryWarn_WhitespaceKey_ThrowsArgumentException()
    {
        var sut = new WarnOnce();
        Assert.Throws<ArgumentException>(() => sut.TryWarn("   ", () => { }));
    }

    [Fact]
    public void TryWarn_NullWarnAction_ThrowsArgumentNullException()
    {
        var sut = new WarnOnce();
        Assert.Throws<ArgumentNullException>(() => sut.TryWarn("key", (Action)null!));
    }

    [Fact]
    public void TryWarn_ExceptionOverload_NullEx_ThrowsArgumentNullException()
    {
        var sut = new WarnOnce();
        Assert.Throws<ArgumentNullException>(() => sut.TryWarn("key", (Exception)null!, _ => { }));
    }

    [Fact]
    public void TryWarn_ExceptionOverload_NullWarnAction_ThrowsArgumentNullException()
    {
        var sut = new WarnOnce();
        Assert.Throws<ArgumentNullException>(() => sut.TryWarn("key", new Exception(), (Action<Exception>)null!));
    }
}
