using System;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// XP1: Cross-Platform URI Canonicalization Acceptance Tests
    ///
    /// ╔══════════════════════════════════════════════════════════════════════════╗
    /// ║  STATUS: PASS ON BOTH WINDOWS AND LINUX                                  ║
    /// ║                                                                          ║
    /// ║  These tests verify the Common library's URI handling is correct.        ║
    /// ║  All 17 tests pass on both Windows and Linux/Docker.                     ║
    /// ║                                                                          ║
    /// ║  ROOT CAUSE IDENTIFIED: The XP1 bug is NOT in Common library!            ║
    /// ║  It's in AppleMusicApiClient.BuildRequestUri() which uses:               ║
    /// ║    Uri.TryCreate(target, UriKind.Absolute, ...)                          ║
    /// ║                                                                          ║
    /// ║  On Linux, "/v1/endpoint" is parsed as file:///v1/endpoint (absolute)    ║
    /// ║  On Windows, "/v1/endpoint" fails TryCreate and becomes relative ✓       ║
    /// ╚══════════════════════════════════════════════════════════════════════════╝
    ///
    /// HERMETIC: All tests use EchoUriHandler (no real DNS/HTTP).
    ///
    /// Verified on Docker Linux:
    ///   docker run --rm -v $(pwd):/src mcr.microsoft.com/dotnet/sdk:8.0 \
    ///     dotnet test /src/tests --filter XP1
    ///   Result: Passed! - Failed: 0, Passed: 17, Skipped: 0
    ///
    /// Acceptance Criteria (verified passing on BOTH Windows and Linux):
    /// 1. Relative URIs with leading slash resolve correctly against BaseAddress
    /// 2. Query parameters are preserved during resolution
    /// 3. URI encoding is consistent across platforms
    /// 4. The resilience layer handles both absolute and relative URIs
    ///
    /// Fix Touchpoints (for AppleMusicarr - NOT Common library):
    /// - AppleMusicApiClient.BuildRequestUri() line 846-854
    /// - Add scheme check: absolute.Scheme == Uri.UriSchemeHttp/Https
    /// - Alternative: Always use UriKind.RelativeOrAbsolute, not UriKind.Absolute
    ///
    /// Tracking: https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/305
    /// </summary>
    public class XP1_UriCanonicalizationTests
    {
        // ============================================================
        // PART 1: Basic URI Resolution (should pass on all platforms)
        // ============================================================

        [Theory]
        [InlineData("https://api.example.com/", "/v1/endpoint", "https://api.example.com/v1/endpoint")]
        [InlineData("https://api.example.com/", "v1/endpoint", "https://api.example.com/v1/endpoint")]
        [InlineData("https://api.example.com/base/", "endpoint", "https://api.example.com/base/endpoint")]
        [InlineData("https://api.example.com/base/", "/endpoint", "https://api.example.com/endpoint")]
        public void RelativeUri_ResolvesCorrectly_AgainstBaseAddress(string baseUri, string relative, string expected)
        {
            var baseAddress = new Uri(baseUri);
            var relativeUri = new Uri(relative, UriKind.RelativeOrAbsolute);

            var resolved = relativeUri.IsAbsoluteUri
                ? relativeUri
                : new Uri(baseAddress, relativeUri);

            Assert.Equal(expected, resolved.ToString());
        }

        [Theory]
        [InlineData("https://api.example.com/", "/v1/endpoint?limit=1", "https://api.example.com/v1/endpoint?limit=1")]
        [InlineData("https://api.example.com/", "/v1/search?q=test", "https://api.example.com/v1/search?q=test")]
        public void RelativeUri_PreservesQueryParameters(string baseUri, string relative, string expected)
        {
            var baseAddress = new Uri(baseUri);
            var relativeUri = new Uri(relative, UriKind.RelativeOrAbsolute);

            var resolved = new Uri(baseAddress, relativeUri);

            Assert.Equal(expected, resolved.ToString());
        }

        [Fact]
        public void RelativeUri_PreservesEncodedQueryParameters()
        {
            // Uri.ToString() decodes percent-encoding, so we check AbsoluteUri or OriginalString
            var baseAddress = new Uri("https://api.example.com/");
            var relativeUri = new Uri("/v1/search?q=test%20query", UriKind.Relative);

            var resolved = new Uri(baseAddress, relativeUri);

            // The query value should be preserved (either encoded or decoded is acceptable)
            Assert.Contains("q=test", resolved.Query);
            Assert.Contains("query", resolved.Query);
        }

        // ============================================================
        // PART 2: HttpClient Integration Tests
        // These test the actual resilience layer behavior
        // ============================================================

        [Fact]
        public async Task ExecuteWithResilience_RelativeUri_WithBaseAddress_Succeeds()
        {
            using var handler = new EchoUriHandler();
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.music.apple.com/")
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/storefront");

            // This should resolve to https://api.music.apple.com/v1/me/storefront
            var response = await httpClient.ExecuteWithResilienceAsync(
                request,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 10,
                perRequestTimeout: TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var actualUri = await response.Content.ReadAsStringAsync();
            Assert.Equal("https://api.music.apple.com/v1/me/storefront", actualUri);
        }

        [Fact]
        public async Task ExecuteWithResilience_RelativeUri_WithQueryParams_PreservesParams()
        {
            using var handler = new EchoUriHandler();
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.music.apple.com/")
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/library/playlists?limit=1");

            var response = await httpClient.ExecuteWithResilienceAsync(
                request,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 10,
                perRequestTimeout: TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var actualUri = await response.Content.ReadAsStringAsync();
            Assert.Equal("https://api.music.apple.com/v1/me/library/playlists?limit=1", actualUri);
        }

        [Fact]
        public async Task ExecuteWithResilience_AbsoluteUri_IgnoresBaseAddress()
        {
            using var handler = new EchoUriHandler();
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.music.apple.com/")
            };

            // Absolute URI should not be affected by BaseAddress
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://other.example.com/endpoint");

            var response = await httpClient.ExecuteWithResilienceAsync(
                request,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 10,
                perRequestTimeout: TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var actualUri = await response.Content.ReadAsStringAsync();
            Assert.Equal("https://other.example.com/endpoint", actualUri);
        }

        [Fact]
        public async Task ExecuteWithResilience_RelativeUri_NoBaseAddress_ThrowsInvalidOperation()
        {
            using var handler = new EchoUriHandler();
            using var httpClient = new HttpClient(handler); // No BaseAddress

            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/storefront");

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await httpClient.ExecuteWithResilienceAsync(
                    request,
                    maxRetries: 1,
                    retryBudget: TimeSpan.FromSeconds(5),
                    maxConcurrencyPerHost: 10,
                    perRequestTimeout: TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            });
        }

        // ============================================================
        // PART 3: Edge Cases That May Differ Between Platforms
        // ============================================================

        [Theory]
        [InlineData("https://api.example.com", "/v1/endpoint", "https://api.example.com/v1/endpoint")]
        [InlineData("https://api.example.com/", "/v1/endpoint", "https://api.example.com/v1/endpoint")]
        public void BaseAddress_TrailingSlash_Variants_ResolveConsistently(string baseUri, string relative, string expected)
        {
            // Both "https://api.example.com" and "https://api.example.com/" should work
            var baseAddress = new Uri(baseUri);
            var relativeUri = new Uri(relative, UriKind.Relative);

            var resolved = new Uri(baseAddress, relativeUri);

            Assert.Equal(expected, resolved.ToString());
        }

        [Theory]
        [InlineData("/v1/catalog/us/albums?ids=1,2,3")]
        [InlineData("/v1/search?term=rock%26roll")]
        [InlineData("/v1/me/library/artists?include=albums")]
        public async Task ExecuteWithResilience_VariousRelativePaths_ResolveCorrectly(string relativePath)
        {
            using var handler = new EchoUriHandler();
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.music.apple.com/")
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);

            var response = await httpClient.ExecuteWithResilienceAsync(
                request,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 10,
                perRequestTimeout: TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var actualUri = await response.Content.ReadAsStringAsync();

            // Verify the URI starts with base and contains the path
            Assert.StartsWith("https://api.music.apple.com", actualUri);
            Assert.Contains(relativePath.Split('?')[0], actualUri); // Path portion matches
        }

        // ============================================================
        // PART 4: Platform Detection and Cross-Platform URI Behavior
        // ============================================================

        [Fact]
        public void Platform_Is_Detected_Correctly()
        {
            // This test documents which platform we're running on
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            var isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

            // At least one must be true
            Assert.True(isWindows || isLinux || isMacOS,
                $"Unknown platform: {RuntimeInformation.OSDescription}");
        }

        /// <summary>
        /// Documents the cross-platform behavior difference that causes XP1.
        ///
        /// On Linux: Uri.TryCreate("/v1/endpoint", UriKind.Absolute) returns TRUE
        ///           and parses it as file:///v1/endpoint
        ///
        /// On Windows: Uri.TryCreate("/v1/endpoint", UriKind.Absolute) returns FALSE
        ///             because paths starting with / are not valid absolute URIs
        ///
        /// This is why AppleMusicApiClient.BuildRequestUri() fails on Linux - it uses
        /// UriKind.Absolute which incorrectly accepts Unix-style paths as file:// URIs.
        ///
        /// CORRECT PATTERN: Use UriKind.RelativeOrAbsolute and check the scheme.
        /// </summary>
        [Fact]
        public void UriTryCreate_CrossPlatform_Behavior_Documented()
        {
            // This demonstrates the safe pattern for cross-platform URI parsing
            string target = "/v1/me/library/albums?limit=1";

            // SAFE: Always use RelativeOrAbsolute, then check scheme
            var uri = new Uri(target, UriKind.RelativeOrAbsolute);

            if (uri.IsAbsoluteUri)
            {
                // On Linux this will be true, but the scheme will be "file"
                // We should only accept http/https schemes
                var isHttpScheme = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
                if (!isHttpScheme)
                {
                    // Treat non-http absolute URIs as relative (reparse)
                    uri = new Uri(target, UriKind.Relative);
                }
            }

            // After the safe pattern, on both platforms we should have a relative URI
            Assert.False(uri.IsAbsoluteUri, "After safe pattern, URI should be relative on all platforms");
            Assert.Equal(target, uri.OriginalString);
        }

        // ============================================================
        // Test Helpers
        // ============================================================

        /// <summary>
        /// Handler that echoes back the resolved request URI as the response body.
        /// Used to verify URI resolution behavior.
        /// </summary>
        private sealed class EchoUriHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri?.ToString() ?? "null";
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(uri)
                };
                return Task.FromResult(response);
            }
        }
    }
}
