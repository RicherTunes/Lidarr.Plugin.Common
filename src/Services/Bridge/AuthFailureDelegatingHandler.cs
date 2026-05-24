using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// <see cref="DelegatingHandler"/> that auto-wires an <see cref="AuthFailureGate"/>
/// into any <see cref="HttpClient"/> built via <c>IHttpClientFactory.AddHttpMessageHandler</c>.
///
/// Without this handler, plugins have to wrap every API method (or every adapter)
/// in identical try/catch boilerplate around <see cref="AuthFailureGate.EnsureCanProceed"/>
/// and <see cref="AuthFailureGate.Handler"/>. With it, the HTTP layer itself:
///   1. Calls <see cref="AuthFailureGate.EnsureCanProceed"/> before sending.
///   2. Marks the handler bad if the response is 401/403.
///   3. Marks the handler healthy (success) on any successful 2xx so the gate
///      auto-recovers after the user re-credentials and the next attempt
///      succeeds — no plugin-side code needed for the happy-path reset.
///
/// Pair with the existing <see cref="AuthFailureGate"/> registration:
/// <code>
/// services.AddSingleton(sp => new AuthFailureGate(sp.GetRequiredService&lt;IAuthFailureHandler&gt;()));
/// services.AddTransient&lt;AuthFailureDelegatingHandler&gt;();
/// services.AddHttpClient&lt;IMyApiClient, MyApiClient&gt;()
///     .AddHttpMessageHandler&lt;AuthFailureDelegatingHandler&gt;();
/// </code>
/// </summary>
public sealed class AuthFailureDelegatingHandler : DelegatingHandler
{
    private readonly AuthFailureGate _gate;

    public AuthFailureDelegatingHandler(AuthFailureGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Capture the probe slot timestamp (if any) so we can refund it on
        // pre-network failures — otherwise a cancelled/DNS-failed request
        // burns the slot for the full interval without ever reaching the
        // upstream, defeating the gate's purpose.
        DateTimeOffset? probeSlotTimestamp = null;
        if (!_gate.IsHealthy)
        {
            probeSlotTimestamp = _gate.AcquireProbeSlotWithTimestamp();
            if (probeSlotTimestamp is null)
            {
                _gate.EnsureCanProceed(); // throws AuthGatedException with RetryAfter
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await _gate.Handler.HandleFailureAsync(new AuthFailure
            {
                ErrorCode = ex.StatusCode?.ToString(),
                Message = ex.Message,
            }, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception) when (probeSlotTimestamp is not null)
        {
            // Pre-network failure (cancellation, DNS, TLS, etc.): the slot
            // was committed but no upstream response was observed, so we
            // learned nothing. Refund so the operator's recovery window
            // doesn't get burned for free.
            _gate.RefundProbeSlot(probeSlotTimestamp.Value);
            throw;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await _gate.Handler.HandleFailureAsync(new AuthFailure
            {
                ErrorCode = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Message = $"Upstream returned {(int)response.StatusCode} {response.ReasonPhrase}",
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (response.IsSuccessStatusCode && !_gate.IsHealthy)
        {
            // Recovery: a 2xx after a latched-bad state means the user
            // re-credentialed (or auto-refresh worked). Clear the latch so
            // the next call is not rate-limited by the probe interval.
            await _gate.Handler.HandleSuccessAsync(cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
