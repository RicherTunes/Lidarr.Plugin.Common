using System.Collections.Generic;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// F-01: SSRF guard for provider-supplied media URLs. Streaming plugins fetch URLs from hostile-controllable
    /// manifests/responses; this guard must block loopback, link-local/metadata, private, CGNAT, multicast and
    /// mapped-IPv6 destinations (and non-https / userinfo) before any fetch, while allowing real public CDNs.
    /// </summary>
    public sealed class RemoteMediaUriGuardTests
    {
        [Theory]
        // Public destinations (literal IPs — no DNS) — allowed.
        [InlineData("https://8.8.8.8/seg.m4s", true)]
        [InlineData("https://[2606:4700:4700::1111]/seg.m4s", true)]
        // Scheme / userinfo (literal host to avoid DNS).
        [InlineData("http://8.8.8.8/seg.m4s", false)]              // http blocked by default
        [InlineData("ftp://cdn.example.com/seg.m4s", false)]
        [InlineData("file:///etc/passwd", false)]
        [InlineData("https://user:pass@cdn.example.com/seg.m4s", false)]
        // Loopback.
        [InlineData("https://127.0.0.1/x", false)]
        [InlineData("https://127.0.0.5:8080/x", false)]
        [InlineData("https://[::1]/x", false)]
        // Link-local + cloud metadata.
        [InlineData("https://169.254.169.254/latest/meta-data/", false)]
        [InlineData("https://metadata.google.internal/computeMetadata/v1/", false)]
        // RFC1918 private.
        [InlineData("https://10.0.0.5/x", false)]
        [InlineData("https://172.16.0.1/x", false)]
        [InlineData("https://172.31.255.1/x", false)]
        [InlineData("https://192.168.1.1/x", false)]
        // CGNAT, unspecified, broadcast, multicast, reserved.
        [InlineData("https://100.64.0.1/x", false)]
        [InlineData("https://0.0.0.0/x", false)]
        [InlineData("https://255.255.255.255/x", false)]
        [InlineData("https://224.0.0.1/x", false)]
        // IPv4-mapped IPv6 bypass attempt.
        [InlineData("https://[::ffff:127.0.0.1]/x", false)]
        [InlineData("https://[::ffff:10.0.0.1]/x", false)]
        // ULA + link-local IPv6.
        [InlineData("https://[fc00::1]/x", false)]
        [InlineData("https://[fe80::1]/x", false)]
        // Malformed.
        [InlineData("not a url", false)]
        [InlineData("", false)]
        public void Validate_StrictPolicy(string url, bool expectedAllowed)
        {
            var result = RemoteMediaUriGuard.Validate(url, RemoteMediaUriPolicy.Strict);

            Assert.Equal(expectedAllowed, result.IsAllowed);
            if (!expectedAllowed)
            {
                Assert.False(string.IsNullOrEmpty(result.Reason));
            }
        }

        [Fact]
        public void Validate_LocalhostHostname_ResolvesToLoopback_Blocked()
        {
            // DNS resolution path: localhost → 127.0.0.1/::1.
            Assert.False(RemoteMediaUriGuard.Validate("https://localhost/x", RemoteMediaUriPolicy.Strict).IsAllowed);
        }

        // Deterministic DNS stubs (no real network).
        private static System.Net.IPAddress[] ResolvePublic(string _) => new[] { System.Net.IPAddress.Parse("8.8.8.8") };
        private static System.Net.IPAddress[] ResolvePrivate(string _) => new[] { System.Net.IPAddress.Parse("10.0.0.5") };

        [Fact]
        public void Validate_AllowHttp_PermitsHttpToPublic()
        {
            var policy = new RemoteMediaUriPolicy { AllowHttp = true };
            Assert.True(RemoteMediaUriGuard.Validate("http://8.8.8.8/x", policy).IsAllowed);
            // ...but private is still blocked unless AllowPrivateNetworks.
            Assert.False(RemoteMediaUriGuard.Validate("http://127.0.0.1/x", policy).IsAllowed);
        }

        [Fact]
        public void Validate_PublicHostname_ResolvesPublic_Allowed()
        {
            Assert.True(RemoteMediaUriGuard.Validate("https://cdn.example.com/seg.m4s", RemoteMediaUriPolicy.Strict, ResolvePublic).IsAllowed);
        }

        [Fact]
        public void Validate_DnsRebinding_PublicHostnameResolvesPrivate_Blocked()
        {
            // A public-looking host that resolves to an internal IP must be rejected.
            var result = RemoteMediaUriGuard.Validate("https://totally-legit-cdn.com/seg.m4s", RemoteMediaUriPolicy.Strict, ResolvePrivate);
            Assert.False(result.IsAllowed);
        }

        [Fact]
        public void Validate_AllowPrivateNetworks_PermitsLoopback_ForLocalProviders()
        {
            var policy = new RemoteMediaUriPolicy { AllowHttp = true, AllowPrivateNetworks = true };
            Assert.True(RemoteMediaUriGuard.Validate("http://127.0.0.1:11434/api", policy).IsAllowed);
            Assert.True(RemoteMediaUriGuard.Validate("http://localhost:1234/v1", policy).IsAllowed);
        }

        [Fact]
        public void Validate_AllowedHostSuffixes_RestrictsToCdn()
        {
            var policy = new RemoteMediaUriPolicy { AllowedHostSuffixes = new List<string> { ".trusted-cdn.com" } };
            Assert.True(RemoteMediaUriGuard.Validate("https://a.trusted-cdn.com/x", policy, ResolvePublic).IsAllowed);
            Assert.False(RemoteMediaUriGuard.Validate("https://evil.example.com/x", policy, ResolvePublic).IsAllowed);
        }
    }
}
