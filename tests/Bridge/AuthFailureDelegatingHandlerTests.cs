using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="AuthFailureDelegatingHandler"/> — the auto-wire
/// surface that drops <see cref="AuthFailureGate"/> behavior into any
/// <see cref="HttpClient"/> via <c>AddHttpMessageHandler</c>. The handler
/// is what lets a plugin gate its entire HTTP layer in one DI line instead
/// of try/catch boilerplate around every API method.
/// </summary>
public sealed class AuthFailureDelegatingHandlerTests
{
    private static (HttpClient Client, DefaultAuthFailureHandler Handler, AuthFailureGate Gate, StubHandler Stub)
        Create(TimeSpan? probeInterval = null)
    {
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        var gate = new AuthFailureGate(handler, TimeProvider.System,
            probeInterval ?? TimeSpan.FromMinutes(5),
            NullLogger<AuthFailureGate>.Instance);
        var stub = new StubHandler();
        var subject = new AuthFailureDelegatingHandler(gate) { InnerHandler = stub };
        var client = new HttpClient(subject) { BaseAddress = new Uri("https://test.invalid/") };
        return (client, handler, gate, stub);
    }

    [Fact]
    public async Task Send_OnUnauthorizedResponse_LatchesAuthBad()
    {
        var (client, handler, _, stub) = Create();
        stub.NextStatus = HttpStatusCode.Unauthorized;

        using var resp = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(AuthStatus.Failed, handler.Status);
    }

    [Fact]
    public async Task Send_OnForbiddenResponse_LatchesAuthBad()
    {
        var (client, handler, _, stub) = Create();
        stub.NextStatus = HttpStatusCode.Forbidden;

        using var resp = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(AuthStatus.Failed, handler.Status);
    }

    [Fact]
    public async Task Send_OnTransientServerError_DoesNotLatchAuthBad()
    {
        var (client, handler, _, stub) = Create();
        await handler.HandleSuccessAsync();
        stub.NextStatus = HttpStatusCode.InternalServerError;

        using var resp = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(AuthStatus.Authenticated, handler.Status);
    }

    [Fact]
    public async Task Send_AfterLatchedBad_AndProbeStillFails_ShortCircuitsWithAuthGatedException()
    {
        // Setup: gate is latched bad. Probe slot is allowed for the first call,
        // but the upstream is STILL 401 (user hasn't re-credentialed yet) so
        // the latch stays bad. The SECOND call must short-circuit without
        // hitting the network — this is the amplification fix for the
        // qobuzarr IP-ban incident.
        var (client, handler, _, stub) = Create();
        await handler.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "test" });
        stub.NextStatus = HttpStatusCode.Unauthorized;
        stub.ResetCallCount();

        // First call uses the probe slot.
        using (var probeResp = await client.GetAsync("/x"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, probeResp.StatusCode);
        }
        Assert.Equal(1, stub.CallCount);

        // Second call inside the probe interval must short-circuit.
        await Assert.ThrowsAsync<AuthGatedException>(() => client.GetAsync("/x"));
        Assert.Equal(1, stub.CallCount); // network was NOT called

        // And 15 more — confirms the amplification stop holds.
        for (var i = 0; i < 15; i++)
        {
            await Assert.ThrowsAsync<AuthGatedException>(() => client.GetAsync("/x"));
        }
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task Send_RecoversAfterLatchedBad_When200Returned()
    {
        var (client, handler, _, stub) = Create();
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        stub.NextStatus = HttpStatusCode.OK;

        // Probe slot allows one network attempt — the upstream now returns 200
        // (user re-credentialed). The handler must clear the latch so subsequent
        // calls are not gated.
        using (var probeResp = await client.GetAsync("/x"))
        {
            Assert.Equal(HttpStatusCode.OK, probeResp.StatusCode);
        }
        Assert.Equal(AuthStatus.Authenticated, handler.Status);

        // Next call should NOT short-circuit — auth is healthy again.
        using var next = await client.GetAsync("/y");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task Send_HappyPath_DoesNotChangeAuthStatus_FromUnknown()
    {
        // Initial Unknown state must stay Unknown after a 2xx — the handler
        // only flips Unknown→Authenticated when it had previously been bad.
        // Otherwise every successful request would noisily call HandleSuccessAsync.
        var (client, handler, _, stub) = Create();
        stub.NextStatus = HttpStatusCode.OK;

        using var resp = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(AuthStatus.Unknown, handler.Status);
    }

    [Fact]
    public async Task Send_OnHttpRequestExceptionWith401_LatchesAuthBad()
    {
        var (client, handler, _, stub) = Create();
        stub.ThrowFactory = () => new HttpRequestException("simulated", inner: null, HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/x"));
        Assert.Equal(AuthStatus.Failed, handler.Status);
    }

    [Fact]
    public async Task Send_OnHttpRequestExceptionWithoutAuthStatus_DoesNotLatch()
    {
        var (client, handler, _, stub) = Create();
        await handler.HandleSuccessAsync();
        // ConnectFailure surfaces as HttpRequestException with no StatusCode.
        stub.ThrowFactory = () => new HttpRequestException("connection failed");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/x"));
        Assert.Equal(AuthStatus.Authenticated, handler.Status);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode NextStatus { get; set; } = HttpStatusCode.OK;
        public Func<Exception>? ThrowFactory { get; set; }
        public int CallCount { get; private set; }
        public void ResetCallCount() => CallCount = 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowFactory is not null) throw ThrowFactory();
            return Task.FromResult(new HttpResponseMessage(NextStatus));
        }
    }
}
