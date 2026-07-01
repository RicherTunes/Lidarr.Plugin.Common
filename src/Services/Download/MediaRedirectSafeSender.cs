using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Sends a media (download) request while keeping the <see cref="RemoteMediaUriGuard"/> SSRF policy in
    /// force across redirects — closing the gap where the initial URL is validated but a hostile/compromised
    /// CDN or manifest redirects to an internal host (R2-01).
    ///
    /// <para>Works regardless of the injected <see cref="HttpClient"/>'s <c>AllowAutoRedirect</c> setting:</para>
    /// <list type="bullet">
    /// <item><b>Auto-redirect OFF</b> (preferred): each 3xx <c>Location</c> is validated against the policy
    /// before the next hop is issued, so an internal-host redirect target is refused <i>before</i> any request
    /// reaches it.</item>
    /// <item><b>Auto-redirect ON</b> (defense-in-depth): the client follows internally, but the final
    /// <see cref="HttpResponseMessage.RequestMessage"/> URI is validated before the body is read, so a response
    /// from an internal host is refused rather than written to disk.</item>
    /// </list>
    /// Only GET/HEAD media flows are intended; redirect methods follow the usual 301/302/303→GET, 307/308→preserve.
    /// </summary>
    public static class MediaRedirectSafeSender
    {
        /// <summary>Default maximum redirect hops for a media fetch.</summary>
        public const int DefaultMaxRedirects = 10;

        /// <summary>
        /// Sends <paramref name="request"/> via <paramref name="client"/>, validating every redirect hop and
        /// the final resolved URI against <paramref name="policy"/>. Throws <see cref="InvalidOperationException"/>
        /// if any hop or the final URI is blocked, or if the hop budget is exhausted.
        /// </summary>
        public static async Task<HttpResponseMessage> SendValidatedAsync(
            HttpClient client,
            HttpRequestMessage request,
            RemoteMediaUriPolicy policy,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
            int maxRedirects = DefaultMaxRedirects,
            CancellationToken cancellationToken = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (request is null) throw new ArgumentNullException(nameof(request));
            policy ??= RemoteMediaUriPolicy.Strict;

            var current = request;
            for (var hop = 0; ; hop++)
            {
                // Validate the target BEFORE issuing the request — refuse a private/internal/non-https host
                // up-front rather than only catching it on the response. This covers the FIRST hop (the initial
                // URL) too, so a direct request to an internal host is never sent at all (R2-01 follow-up).
                if (current.RequestUri is { } targetUri && !RemoteMediaUriGuard.Validate(targetUri, policy).IsAllowed)
                {
                    if (current != request) current.Dispose();
                    throw new InvalidOperationException(
                        $"Refusing media request to an unsafe URL: {Redact(targetUri)}.");
                }

                var response = await client.SendAsync(current, completionOption, cancellationToken).ConfigureAwait(false);

                // Defense-in-depth: if the client auto-followed redirects internally, RequestMessage.RequestUri
                // is the final landing URI. Refuse it before the caller consumes the body.
                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is not null && !RemoteMediaUriGuard.Validate(finalUri, policy).IsAllowed)
                {
                    response.Dispose();
                    throw new InvalidOperationException(
                        $"Refusing media response from a redirected unsafe URL: {Redact(finalUri)}.");
                }

                var status = (int)response.StatusCode;
                var isRedirect = status is 301 or 302 or 303 or 307 or 308;
                var location = response.Headers.Location;
                if (!isRedirect || location is null)
                {
                    return response; // final (non-redirect) response — already validated above.
                }

                // Manual follow (auto-redirect OFF path): validate the target BEFORE issuing the next hop.
                if (hop >= maxRedirects)
                {
                    response.Dispose();
                    throw new InvalidOperationException($"Too many media redirects (> {maxRedirects}).");
                }

                var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                if (!RemoteMediaUriGuard.Validate(target, policy).IsAllowed)
                {
                    response.Dispose();
                    throw new InvalidOperationException(
                        $"Refusing redirect to an unsafe media URL: {Redact(target)}.");
                }

                // 301/302/303 downgrade unsafe methods to GET; 307/308 preserve the method. Media is GET/HEAD,
                // so a fresh request without a body is correct for every case here.
                var method = status is 307 or 308 ? current.Method : HttpMethod.Get;
                var next = new HttpRequestMessage(method, target);
                CopyForwardableHeaders(current, next);

                response.Dispose();
                if (current != request) current.Dispose();
                current = next;
            }
        }

        private static void CopyForwardableHeaders(HttpRequestMessage from, HttpRequestMessage to)
        {
            foreach (var h in from.Headers)
            {
                // Drop Authorization on cross-origin redirects to avoid leaking credentials to the new host.
                if (string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !IsSameOrigin(from.RequestUri, to.RequestUri))
                {
                    continue;
                }
                to.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        private static bool IsSameOrigin(Uri? left, Uri? right)
        {
            return left is not null &&
                   right is not null &&
                   string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.DnsSafeHost, right.DnsSafeHost, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        private static string Redact(Uri uri) => $"{uri.Scheme}://{uri.Host}";
    }
}
