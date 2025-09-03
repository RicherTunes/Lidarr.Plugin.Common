// Example only: demonstrates wiring NzbDrone.Common.Http to GenericResilienceExecutor
// Not compiled by default.
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using NzbDrone.Common.Http;

namespace Lidarr.Plugin.Common.Examples
{
    public static class NzbDroneResilienceAdapterExample
    {
        public static async Task<HttpResponse> ExecuteAsync(IHttpClient httpClient, HttpRequest request, CancellationToken ct)
        {
            return await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequest, HttpResponse>(
                request,
                sendAsync: async (req, token) => await httpClient.ExecuteAsync(req),
                cloneRequestAsync: async (req) => Clone(req),
                getHost: (req) => req.Url?.Host,
                getStatusCode: (resp) => (int)resp.StatusCode,
                getRetryAfterDelay: (resp) =>
                {
                    try
                    {
                        var header = resp.Headers?.GetValues("Retry-After")?.FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(header)) return null;
                        if (int.TryParse(header, out var seconds)) return TimeSpan.FromSeconds(Math.Max(0, seconds));
                        if (DateTimeOffset.TryParse(header, out var when))
                        {
                            var delta = when - DateTimeOffset.UtcNow;
                            if (delta > TimeSpan.Zero) return delta;
                        }
                    }
                    catch { }
                    return null;
                },
                maxRetries: 5,
                retryBudget: TimeSpan.FromSeconds(60),
                maxConcurrencyPerHost: 6,
                cancellationToken: ct);
        }

        private static HttpRequest Clone(HttpRequest request)
        {
            var builder = new HttpRequestBuilder(request.Url);
            if (request.Method == HttpMethod.Post) builder.Post();
            foreach (var header in request.Headers.All())
            {
                builder.SetHeader(header.Key, header.Value);
            }
            builder.SetQueryString(request.Url.Query);
            if (request.HasHttpContent)
            {
                builder.SetContent(request.ContentData,
                    request.Headers.ContentType,
                    request.Headers.ContentLength);
            }
            return builder.Build();
        }
    }
}

