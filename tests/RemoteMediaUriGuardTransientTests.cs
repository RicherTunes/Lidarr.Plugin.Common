using System.Net;
using System.Net.Sockets;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Transient-vs-unsafe distinction for <see cref="RemoteMediaUriGuard"/>. A DNS-resolution FAILURE (resolver
    /// throws, or returns no addresses) means safety could not be confirmed, but it is a network/DNS blip — NOT a
    /// hostile private-IP target — so it must be classified TRANSIENT (retryable), while every genuine security
    /// block (resolved-to-private, literal private IP, non-https, metadata host) stays a HARD, non-transient block.
    /// This is the fix for the live regression where a transient DNS failure permanently failed every Tidal
    /// download.
    /// </summary>
    public sealed class RemoteMediaUriGuardTransientTests
    {
        private static IPAddress[] ThrowSocket(string _) => throw new SocketException(11001); // host not found
        private static IPAddress[] ResolveEmpty(string _) => System.Array.Empty<IPAddress>();
        private static System.Func<string, IPAddress[]> ResolveTo(string ip) => _ => new[] { IPAddress.Parse(ip) };

        [Fact]
        public void ResolverThrowsSocketException_IsTransient_NotAllowed()
        {
            var result = RemoteMediaUriGuard.Validate("https://cdn.example.com/seg.m4s", RemoteMediaUriPolicy.Strict, ThrowSocket);

            Assert.False(result.IsAllowed);
            Assert.True(result.IsTransient);
            Assert.False(string.IsNullOrEmpty(result.Reason));
        }

        [Fact]
        public void ResolverReturnsEmpty_IsTransient_NotAllowed()
        {
            var result = RemoteMediaUriGuard.Validate("https://cdn.example.com/seg.m4s", RemoteMediaUriPolicy.Strict, ResolveEmpty);

            Assert.False(result.IsAllowed);
            Assert.True(result.IsTransient);
        }

        // SECURITY: a host that RESOLVES to a private/metadata/loopback IP is a confirmed-unsafe target — it must be
        // a HARD block (IsTransient == false), never relaxed. This is the whole point of the transient distinction.
        [Theory]
        [InlineData("10.0.0.1")]
        [InlineData("169.254.169.254")]
        [InlineData("127.0.0.1")]
        [InlineData("192.168.1.1")]
        public void ResolverReturnsPrivateIp_IsHardBlock_NotTransient(string privateIp)
        {
            var result = RemoteMediaUriGuard.Validate("https://totally-legit-cdn.com/seg.m4s", RemoteMediaUriPolicy.Strict, ResolveTo(privateIp));

            Assert.False(result.IsAllowed);
            Assert.False(result.IsTransient); // SECURITY PRESERVED: resolved-to-private is never transient.
        }

        [Fact]
        public void LiteralPrivateIpHost_IsHardBlock_NotTransient()
        {
            var result = RemoteMediaUriGuard.Validate("https://10.0.0.1/x", RemoteMediaUriPolicy.Strict);

            Assert.False(result.IsAllowed);
            Assert.False(result.IsTransient);
        }

        [Fact]
        public void NonHttpsScheme_IsHardBlock_NotTransient()
        {
            var result = RemoteMediaUriGuard.Validate("ftp://cdn.example.com/x", RemoteMediaUriPolicy.Strict);

            Assert.False(result.IsAllowed);
            Assert.False(result.IsTransient);
        }

        [Fact]
        public void MetadataHost_IsHardBlock_NotTransient()
        {
            var result = RemoteMediaUriGuard.Validate("https://metadata.google.internal/x", RemoteMediaUriPolicy.Strict);

            Assert.False(result.IsAllowed);
            Assert.False(result.IsTransient);
        }

        [Fact]
        public void ResolverReturnsPublicIp_IsAllowed_NotTransient()
        {
            var result = RemoteMediaUriGuard.Validate("https://cdn.example.com/seg.m4s", RemoteMediaUriPolicy.Strict, ResolveTo("8.8.8.8"));

            Assert.True(result.IsAllowed);
            Assert.False(result.IsTransient);
        }
    }
}
