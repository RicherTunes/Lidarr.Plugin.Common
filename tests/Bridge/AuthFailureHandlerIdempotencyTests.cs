using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// R2-12: AuthFailureDelegatingHandler calls HandleSuccessAsync on every
/// 2xx while the gate is bad — that can fire multiple times during the
/// recovery transition under concurrent probes. The interface contract
/// must declare idempotency a requirement so custom handlers don't
/// double-count metrics / re-emit events / reset DB rows etc.
/// </summary>
public sealed class AuthFailureHandlerIdempotencyTests
{
    [Fact]
    public async Task DefaultAuthFailureHandler_HandleSuccessAsync_IsIdempotent()
    {
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        Assert.Equal(AuthStatus.Failed, handler.Status);

        // Calling HandleSuccessAsync 10 times in a row must leave the handler
        // in a single, consistent state — no oscillation, no exception, no
        // observable side effect beyond the first call.
        for (var i = 0; i < 10; i++)
        {
            await handler.HandleSuccessAsync();
        }

        Assert.Equal(AuthStatus.Authenticated, handler.Status);
        Assert.Null(handler.LastFailure);
    }

    [Fact]
    public async Task DefaultAuthFailureHandler_HandleSuccessAsync_FromUnknownIsIdempotent()
    {
        // Starting from Unknown, repeated success calls must not produce
        // a transition→Authenticated→Authenticated→... loop.
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);

        for (var i = 0; i < 10; i++)
        {
            await handler.HandleSuccessAsync();
        }

        Assert.Equal(AuthStatus.Authenticated, handler.Status);
    }
}
