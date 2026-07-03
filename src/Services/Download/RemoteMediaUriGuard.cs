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

        /// <summary>Optional DNS resolver used when <see cref="ResolveDns"/> is on and no explicit resolver is
        /// passed to <c>Validate</c>. Lets production wire the guard with a policy alone while keeping
        /// <see cref="ResolveDns"/> = true, and lets tests inject a deterministic resolver instead of weakening
        /// the policy to <c>ResolveDns = false</c> (R2-02). Null ⇒ real <see cref="System.Net.Dns"/>.</summary>
        public Func<string, IPAddress[]>? DnsResolver { get; init; }

        /// <summary>Strict default: https only, public destinations only.</summary>
        public static RemoteMediaUriPolicy Strict { get; } = new();
    }

    /// <summary>
    /// Outcome of a guard check.
    /// <para><see cref="IsTransient"/> distinguishes a <b>transient</b> block (DNS resolution itself FAILED, so
    /// the destination could not be confirmed safe — a network/DNS blip, not a hostile target) from a HARD
    /// security block (non-https scheme, userinfo, cloud-metadata host, allowed-suffix miss, or an address that
    /// resolved to a private/loopback/link-local/reserved IP). A transient result is <see cref="IsAllowed"/> =
    /// false (we still refuse to fetch on an unconfirmed host) but callers should surface it as a RETRYABLE
    /// network error rather than a permanent refusal — the SSRF guarantee is that we only relax the case where
    /// resolution FAILED, never a case where we resolved to something unsafe.</para>
    /// </summary>
    public readonly record struct UriGuardResult(bool IsAllowed, string? Reason, bool IsTransient = false)
    {
        public static UriGuardResult Allowed { get; } = new(true, null, false);

        /// <summary>A hard, permanent block (security rejection). Never transient.</summary>
        public static UriGuardResult Blocked(string reason) => new(false, reason, false);

        /// <summary>A transient block: resolution failed so safety could not be confirmed, but this is a
        /// network/DNS failure, not a hostile private-IP target. <see cref="IsAllowed"/> stays false;
        /// <see cref="IsTransient"/> is true so callers can retry instead of permanently failing.</summary>
        public static UriGuardResult Transient(string reason) => new(false, reason, true);
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
    ///
    /// <para><b>Transient vs. unsafe:</b> a DNS-resolution FAILURE (the resolver throws, or returns no addresses)
    /// is reported as a <see cref="UriGuardResult.Transient(string)"/> result — <see cref="UriGuardResult.IsAllowed"/>
    /// stays false (we still refuse to fetch on a host we could not confirm), but <see cref="UriGuardResult.IsTransient"/>
    /// is true so download callers surface it as a RETRYABLE network error instead of a permanent refusal.
    /// This is safe because it relaxes <b>only</b> the case where resolution failed: a host that successfully
    /// resolves to a private/loopback/link-local/reserved address is a confirmed-unsafe target and remains a hard
    /// <see cref="UriGuardResult.Blocked(string)"/> (never transient). Treating a resolution blip as permanent
    /// otherwise fails downloads whenever a CDN host intermittently fails to resolve.</para>
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

            // R2-09: a trailing-dot FQDN ("host." == "host"). Normalize before any host comparison so a trailing
            // dot can't bypass the metadata blocklist or the allowed-suffix check.
            if (host.Length > 1 && host[^1] == '.')
            {
                host = host[..^1];
            }

            if (MetadataHosts.Any(m => string.Equals(host, m, StringComparison.OrdinalIgnoreCase)))
            {
                return UriGuardResult.Blocked("URL targets a cloud metadata host.");
            }

            if (policy.AllowedHostSuffixes is { Count: > 0 } &&
                !policy.AllowedHostSuffixes.Any(s => HostMatchesSuffix(host, s)))
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
                    // Precedence: explicit arg > policy-carried resolver (R2-02) > real DNS.
                    resolved = (dnsResolver ?? policy.DnsResolver ?? Dns.GetHostAddresses)(host);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException)
                {
                    // Resolution FAILED — we cannot confirm the host is safe, but this is a DNS/network failure,
                    // not a resolved-to-private (hostile) target. Classify TRANSIENT so callers retry rather than
                    // permanently failing the download on a DNS blip. (A resolved-to-private address below is a
                    // hard Blocked — security is never relaxed for a confirmed-unsafe target.)
                    return UriGuardResult.Transient("URL host could not be resolved.");
                }

                if (resolved.Length == 0)
                {
                    // Same reasoning: no addresses came back → resolution effectively failed → transient, not a
                    // confirmed-unsafe destination.
                    return UriGuardResult.Transient("URL host resolved to no addresses.");
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

        /// <summary>R2-09: host matches an allowed suffix only on a label (dot) boundary — exact host, or a
        /// real subdomain ending in ".&lt;suffix&gt;". A bare suffix "cdn.com" must not admit "evilcdn.com".
        /// Accepts the suffix written with or without a leading dot.</summary>
        private static bool HostMatchesSuffix(string host, string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return false;
            }

            var bare = suffix[0] == '.' ? suffix[1..] : suffix;
            if (bare.Length == 0)
            {
                return false;
            }

            return string.Equals(host, bare, StringComparison.OrdinalIgnoreCase) ||
                   (host.Length > bare.Length &&
                    host[host.Length - bare.Length - 1] == '.' &&
                    host.EndsWith(bare, StringComparison.OrdinalIgnoreCase));
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
