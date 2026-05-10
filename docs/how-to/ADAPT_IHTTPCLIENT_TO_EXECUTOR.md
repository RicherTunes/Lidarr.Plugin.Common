# How to: adapt Lidarr's `IHttpClient` to `CachingHttpExecutor`

`CachingHttpExecutor` accepts a `System.Net.Http.HttpMessageInvoker` (the base class of `HttpClient`), but
Lidarr-host plugins typically dispatch through `NzbDrone.Common.Http.IHttpClient` so that requests pick up
the host's resilience, telemetry, and rate limiting. This page documents the adapter shape and the policy
choices that go with it.

> **Why a doc, not common code?** The host's `IHttpClient` lives in the Lidarr binaries, not in any
> public NuGet package. Common cannot reference it without taking a host-version dependency or shipping a
> reflection shim, neither of which is worth the surface area. Each plugin already has the type in its
> compile graph — the adapter is ~50 LOC and lives next to the wiring code that needs it. Source: qobuzarr
> Phase 3b adoption feedback.

## The adapter shape

Implement an `HttpMessageInvoker` (or a `DelegatingHandler`) that translates `HttpRequestMessage` into the
host shape and the host response back into `HttpResponseMessage`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.Http;        // host
using NzbDrone.Common.Http.Proxy;  // host

internal sealed class LidarrHttpClientInvoker : HttpMessageInvoker
{
    private readonly IHttpClient _host;

    public LidarrHttpClientInvoker(IHttpClient host)
        : base(new SocketsHttpHandler(), disposeHandler: false)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var hostRequest = await ToHostRequestAsync(request, cancellationToken).ConfigureAwait(false);
        HttpResponse hostResponse;
        try
        {
            hostResponse = await Task.Run(() => _host.Execute(hostRequest), cancellationToken)
                                     .ConfigureAwait(false);
        }
        catch (HttpException ex)
        {
            // The host raises HttpException for non-success statuses *only when configured to throw*.
            // If your IHttpClient is configured to throw, translate to a non-success HttpResponseMessage
            // so the executor's stale-if-error / terminal-eviction paths can run.
            hostResponse = ex.Response;
        }
        return ToHttpResponseMessage(hostResponse);
    }

    private static async Task<HttpRequest> ToHostRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var hostRequest = new HttpRequestBuilder(request.RequestUri!.ToString())
            .SetHeader("User-Agent", request.Headers.UserAgent.ToString() ?? "Lidarr.Plugin")
            .Build();

        hostRequest.Method = request.Method.Method switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new NotSupportedException($"Unsupported method: {request.Method.Method}")
        };

        foreach (var header in request.Headers)
        {
            // Skip headers the host owns (User-Agent, Host, etc.).
            if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            hostRequest.Headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            hostRequest.SetContent(bytes);
            if (request.Content.Headers.ContentType is { } ct2)
            {
                hostRequest.Headers["Content-Type"] = ct2.ToString();
            }
        }

        return hostRequest;
    }

    private static HttpResponseMessage ToHttpResponseMessage(HttpResponse hostResponse)
    {
        var msg = new HttpResponseMessage((HttpStatusCode)(int)hostResponse.StatusCode)
        {
            Content = new ByteArrayContent(hostResponse.ResponseData ?? Array.Empty<byte>())
        };
        foreach (var header in hostResponse.Headers ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            // ContentType / Content-Length and friends belong on Content.Headers, not Headers.
            if (!msg.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                msg.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return msg;
    }
}
```

## Wiring it up

Pair the adapter with `ResiliencePolicy.Passthrough` so the executor's `GenericResilienceExecutor` does not
stack on top of the retries the host already performs:

```csharp
var invoker = new LidarrHttpClientInvoker(hostHttpClient);
var executor = new CachingHttpExecutor(
    invoker:           invoker,
    cache:             cache,
    resiliencePolicy:  ResiliencePolicy.Passthrough,   // host already retries
    policyProvider:    policyProvider,
    conditionalState:  conditionalState,
    timeProvider:      TimeProvider.System,
    logger:            loggerFactory.CreateLogger<CachingHttpExecutor>());
```

## Why no `IHttpExecutorTransport` abstraction?

A common abstraction was considered (an interface that both `HttpMessageInvoker` and the host adapter
implement). It was rejected because:

- The adapter is ~50 LOC and only one host does it (Lidarr). Standardizing the interface would force every
  plugin to take a transport-shaped dependency that nobody else needs.
- `HttpMessageInvoker` is already the .NET-standard transport interface. Adding a parallel one would be
  duplicate surface area for no functional gain.
- The translation is already as thin as possible — there's nothing to factor out.

If you have a second host whose transport doesn't fit `HttpMessageInvoker`, the right move is to start
with the adapter shape above and consolidate the second time the same pattern appears, not before.
