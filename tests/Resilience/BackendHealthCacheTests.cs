using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Resilience;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Resilience;

/// <summary>
/// Unit tests for <see cref="BackendHealthCache"/>.
/// Ported from Brainarr.Tests (16 tests) and extended with Common-specific
/// constructor overloads (custom grace period).
/// </summary>
public class BackendHealthCacheTests
{
    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static FakeTimeProviderBhc MakeFake(DateTimeOffset start) => new(start);

    private static Exception MakeSocketException()
    {
        var socket = new SocketException((int)SocketError.ConnectionRefused);
        return new HttpRequestException("Connection refused", socket);
    }

    private static Exception MakeSocketExceptionDirect()
        => new SocketException((int)SocketError.ConnectionRefused);

    // ------------------------------------------------------------------ //
    // 1. MarkDown → IsKnownDown within grace returns true
    // ------------------------------------------------------------------ //

    [Fact]
    public void IsKnownDown_AfterMarkDown_WithinGrace_ReturnsTrue()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("Ollama", "http://localhost:11434", MakeSocketException());
        fake.Advance(TimeSpan.FromSeconds(1));

        var result = cache.IsKnownDown("Ollama", "http://localhost:11434", out var reason);

        Assert.True(result);
        Assert.NotNull(reason);
        Assert.Contains("known-down", reason);
    }

    // ------------------------------------------------------------------ //
    // 2. MarkDown → IsKnownDown after grace expired returns false
    // ------------------------------------------------------------------ //

    [Fact]
    public void IsKnownDown_AfterMarkDown_GraceExpired_ReturnsFalse()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("Ollama", "http://localhost:11434", MakeSocketException());
        fake.Advance(TimeSpan.FromSeconds(BackendHealthCache.DefaultGraceSeconds + 1));

        var result = cache.IsKnownDown("Ollama", "http://localhost:11434", out var reason);

        Assert.False(result);
        Assert.Null(reason);
    }

    // ------------------------------------------------------------------ //
    // 3. MarkUp clears the down state immediately
    // ------------------------------------------------------------------ //

    [Fact]
    public void IsKnownDown_AfterMarkUp_ReturnsFalse_RegardlessOfGrace()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("LMStudio", "http://localhost:1234", MakeSocketException());
        Assert.True(cache.IsKnownDown("LMStudio", "http://localhost:1234", out _));

        cache.MarkUp("LMStudio", "http://localhost:1234");

        var result = cache.IsKnownDown("LMStudio", "http://localhost:1234", out var reason);
        Assert.False(result);
        Assert.Null(reason);
    }

    // ------------------------------------------------------------------ //
    // 4. Different (provider, url) keys are independent
    // ------------------------------------------------------------------ //

    [Fact]
    public void Keys_AreScopedByProviderAndUrl()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("Ollama", "http://localhost:11434", MakeSocketException());

        Assert.False(cache.IsKnownDown("LMStudio", "http://localhost:1234", out _));
        Assert.False(cache.IsKnownDown("Ollama", "http://localhost:11435", out _));
        Assert.True(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));
    }

    [Fact]
    public void Key_IsCaseInsensitive_ForProviderAndUrl()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("ollama", "http://Localhost:11434", MakeSocketException());

        Assert.True(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));
        Assert.True(cache.IsKnownDown("OLLAMA", "HTTP://LOCALHOST:11434", out _));
    }

    [Fact]
    public void Key_NormalizesTrailingSlash()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("Ollama", "http://localhost:11434/", MakeSocketException());

        Assert.True(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));
    }

    // ------------------------------------------------------------------ //
    // 5. Concurrent MarkDown calls don't corrupt state
    // ------------------------------------------------------------------ //

    [Fact]
    public void ConcurrentMarkDown_DoesNotCorruptState()
    {
        const int threads = 50;
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);
        var ex = MakeSocketException();
        var barrier = new System.Threading.Barrier(threads);
        var tasks = new Task[threads];

        for (int i = 0; i < threads; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                cache.MarkDown("Ollama", "http://localhost:11434", ex);
            });
        }

        Task.WaitAll(tasks);

        Assert.True(cache.IsKnownDown("Ollama", "http://localhost:11434", out var reason));
        Assert.NotNull(reason);
    }

    // ------------------------------------------------------------------ //
    // IsConnectionClassFailure classification
    // ------------------------------------------------------------------ //

    [Fact]
    public void IsConnectionClassFailure_SocketException_ReturnsTrue()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);
        Assert.True(BackendHealthCache.IsConnectionClassFailure(ex));
    }

    [Fact]
    public void IsConnectionClassFailure_HttpRequestExceptionWrappingSocketException_ReturnsTrue()
    {
        var ex = MakeSocketException();
        Assert.True(BackendHealthCache.IsConnectionClassFailure(ex));
    }

    [Fact]
    public void IsConnectionClassFailure_TaskCanceledException_ReturnsFalse()
    {
        Assert.False(BackendHealthCache.IsConnectionClassFailure(new TaskCanceledException()));
    }

    [Fact]
    public void IsConnectionClassFailure_GenericException_ReturnsFalse()
    {
        Assert.False(BackendHealthCache.IsConnectionClassFailure(new InvalidOperationException("some error")));
    }

    [Fact]
    public void IsConnectionClassFailure_Null_ReturnsFalse()
    {
        Assert.False(BackendHealthCache.IsConnectionClassFailure(null));
    }

    [Fact]
    public void MarkDown_WithNonConnectionException_DoesNotMarkDown()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("Ollama", "http://localhost:11434", new Exception("some random error"));

        Assert.False(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));
    }

    // ------------------------------------------------------------------ //
    // 6. Custom grace period
    // ------------------------------------------------------------------ //

    [Fact]
    public void CustomGracePeriod_IsRespected()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        const int customGrace = 5;
        var cache = new BackendHealthCache(fake, customGrace);

        cache.MarkDown("Ollama", "http://localhost:11434", MakeSocketException());
        fake.Advance(TimeSpan.FromSeconds(customGrace - 1));
        Assert.True(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));

        fake.Advance(TimeSpan.FromSeconds(2)); // now past grace
        Assert.False(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));
    }

    [Fact]
    public void DefaultGraceSeconds_Is30()
    {
        Assert.Equal(30, BackendHealthCache.DefaultGraceSeconds);
    }

    // ------------------------------------------------------------------ //
    // 7. DirectSocketException (no HttpRequestException wrapper)
    // ------------------------------------------------------------------ //

    [Fact]
    public void MarkDown_WithDirectSocketException_MarksDown()
    {
        var fake = MakeFake(DateTimeOffset.UtcNow);
        var cache = new BackendHealthCache(fake);

        cache.MarkDown("Ollama", "http://localhost:11434", MakeSocketExceptionDirect());

        Assert.True(cache.IsKnownDown("Ollama", "http://localhost:11434", out _));
    }

    // ------------------------------------------------------------------ //
    // 8. Shared singleton is non-null
    // ------------------------------------------------------------------ //

    [Fact]
    public void Shared_IsNonNull()
    {
        Assert.NotNull(BackendHealthCache.Shared);
    }
}

// ------------------------------------------------------------------ //
// Minimal FakeTimeProvider for deterministic time control in tests.
// ------------------------------------------------------------------ //

internal sealed class FakeTimeProviderBhc : TimeProvider
{
    private DateTimeOffset _utcNow;
    private readonly object _lock = new();

    public FakeTimeProviderBhc(DateTimeOffset start) => _utcNow = start;

    public override DateTimeOffset GetUtcNow() { lock (_lock) return _utcNow; }

    public void Advance(TimeSpan delta) { lock (_lock) _utcNow = _utcNow.Add(delta); }
}
