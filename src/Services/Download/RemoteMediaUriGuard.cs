using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Policy for <see cref="RemoteMediaUriGuard"/>. Defaults are strict (https-only, no private networks),
    /// suitable for provider-controlled media/CDN URLs. Local-LLM/dev providers can opt into http and/or
    /// private networks explicitly and narrowly.
    /// </summary>
    public sealed class RemoteMediaUriPolicy
    {
        /// <summary>Allow plain <c>http</c> in addition to <c>https</c> (default false).</summary>
        public bool AllowHttp { get; init; }

        /// <summary>Allow loopback/private/link-local destinations (default false). Set only for explicitly
        /// local providers (e.g. a self-hosted LLM), and prefer scoping with <see cref="AllowedHostSuffixes"/>.</summary>
        public bool AllowPrivateNetworks { get; init; }

        /// <summary>If set, the host must end with one of these suffixes (case-insensitive), e.g. CDN domains.</summary>
        public IReadOnlyList<string>? AllowedHostSuffixes { get; init; }

        /// <summary>Resolve hostnames and reject if any resolved address is private/blocked (default true).
        /// Mitigates (does not fully prevent — see remarks) DNS-rebinding to internal hosts.</summary>
        public bool ResolveDns { get; init; } = true;

        /// <summary>Strict default: https only, public destinations only.</summary>
        public static RemoteMediaUriPolicy Strict { get; } = new();
    }

    /// <summary>Outcome of a guard check.</summary>
    public readonly record struct UriGuardResult(bool IsAllowed, string? Reason)
    {
        public static UriGuardResult Allowed { get; } = new(true, null);

        public static UriGuardResult Blocked(string reason) => new(false, reason);
    }

    /// <summary>
    /// SSRF guard for provider-supplied media/manifest/segment URLs. Streaming plugins run inside the Lidarr
    /// host and consume provider-controlled URLs (DASH/HLS manifests, CDN segments, file URLs), so a hostile
    /// or compromised response could point at loopback, link-local metadata (169.254.169.254), or RFC1918
    /// hosts the plugin can reach but a normal user cannot. Validate every URL with this guard <b>before</b>
    /// any GET/HEAD/range/probe.
    ///
    /// <para><b>Limitations (be honest, like PathTraversalGuard):</b> a hostname can re-resolve to a private
    /// address between this check and the actual connect (DNS rebinding). For full protection, also disable
    /// automatic redirects and re-validate the resolved address at connection time. This guard removes the
    /// large, easy SSRF surface (literal-IP and naive-DNS targets) and is the shared policy plugins build on.</para>
    /// </summary>
    public static class RemoteMediaUriGuard
    {
        // Cloud metadata endpoints (in addition to the link-local 169.254.169.254, which IsBlocked catches).
        private static readonly string[] MetadataHosts =
        {
            "metadata.google.internal",
            "metadata.goog",
        };

        public static UriGuardResult Validate(string? url, RemoteMediaUriPolicy? policy = null, Func<string, IPAddress[]>? dnsResolver = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return UriGuardResult.Blocked("URL is empty.");
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return UriGuardResult.Blocked("URL is not an absolute URI.");
            }

            return Validate(uri, policy, dnsResolver);
        }

        public static UriGuardResult Validate(Uri? uri, RemoteMediaUriPolicy? policy = null, Func<string, IPAddress[]>? dnsResolver = null)
        {
            policy ??= RemoteMediaUriPolicy.Strict;

            if (uri is null || !uri.IsAbsoluteUri)
            {
                return UriGuardResult.Blocked("URL is not an absolute URI.");
            }

            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var isHttp = uri.Scheme == Uri.UriSchemeHttp;
            if (!isHttps && !(isHttp && policy.AllowHttp))
            {
                return UriGuardResult.Blocked($"Scheme '{uri.Scheme}' is not allowed (expected https{(policy.AllowHttp ? "/http" : "")}).");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return UriGuardResult.Blocked("URL must not contain userinfo (user:pass@host).");
            }

            var host = uri.DnsSafeHost;
            if (string.IsNullOrEmpty(host))
            {
                return UriGuardResult.Blocked("URL has no host.");
            }

            if (MetadataHosts.Any(m => string.Equals(host, m, StringComparison.OrdinalIgnoreCase)))
            {
                return UriGuardResult.Blocked("URL targets a cloud metadata host.");
            }

            if (policy.AllowedHostSuffixes is { Count: > 0 } &&
                !policy.AllowedHostSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                return UriGuardResult.Blocked("URL host is not in the allowed host-suffix list.");
            }

            // Literal IP host: classify directly.
            if (IPAddress.TryParse(host, out var literal))
            {
                if (!policy.AllowPrivateNetworks && IsBlocked(literal))
                {
                    return UriGuardResult.Blocked($"URL targets a non-public IP ({literal}).");
                }

                return UriGuardResult.Allowed;
            }

            // Hostname: optionally resolve and reject if any resolved address is private/blocked.
            if (policy.ResolveDns && !policy.AllowPrivateNetworks)
            {
                IPAddress[] resolved;
                try
                {
                    resolved = (dnsResolver ?? Dns.GetHostAddresses)(host);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException)
                {
                    return UriGuardResult.Blocked("URL host could not be resolved.");
                }

                if (resolved.Length == 0)
                {
                    return UriGuardResult.Blocked("URL host resolved to no addresses.");
                }

                foreach (var addr in resolved)
                {
                    if (IsBlocked(addr))
                    {
                        return UriGuardResult.Blocked($"URL host resolves to a non-public IP ({addr}).");
                    }
                }
            }

            return UriGuardResult.Allowed;
        }

        /// <summary>True if the address is loopback, link-local, private, ULA, multicast, unspecified, or
        /// otherwise not a public unicast destination.</summary>
        public static bool IsBlocked(IPAddress address)
        {
            if (address is null)
            {
                return true;
            }

            // Unwrap IPv4-mapped IPv6 (::ffff:a.b.c.d) so an attacker can't bypass via the mapped form.
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true; // 127.0.0.0/8, ::1
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return true; // 0.0.0.0, ::
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                // 10/8, 172.16/12, 192.168/16 (private); 169.254/16 (link-local incl. metadata);
                // 100.64/10 (CGNAT); 0/8; 240/4 (reserved); 255.255.255.255 (broadcast); 224/4 (multicast).
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    || b[0] == 0
                    || b[0] >= 240
                    || (b[0] >= 224 && b[0] <= 239);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal
                    || address.IsIPv6SiteLocal
                    || address.IsIPv6Multicast
                    || IsIPv6UniqueLocal(address); // fc00::/7
            }

            return true; // unknown family — refuse.
        }

        private static bool IsIPv6UniqueLocal(IPAddress address)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc; // fc00::/7
        }
    }
}
