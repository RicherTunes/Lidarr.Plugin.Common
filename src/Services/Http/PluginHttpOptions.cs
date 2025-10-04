using System.Collections.Generic;
using System.Net.Http;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Well-known HttpRequestMessage.Options keys used by Arr plugins to annotate
    /// outbound requests with endpoint/profile/parameters/auth scope metadata.
    /// </summary>
    /// <remarks>
    /// Key names use the neutral prefix "arr.plugin.http.*" to support reuse across Arr-family plugins.
    /// </remarks>
    public static class PluginHttpOptions
    {
        /// <summary>Normalized API path (e.g., "/v1/catalog/albums").</summary>
        public static readonly HttpRequestOptionsKey<string> EndpointKey = new("arr.plugin.http.endpoint");

        /// <summary>Named profile for resilience/metrics (e.g., "search", "details").</summary>
        public static readonly HttpRequestOptionsKey<string> ProfileKey = new("arr.plugin.http.profile");

        /// <summary>
        /// Canonical query string (sorted ordinal; multivalue keys ordered; percent-encoding normalized),
        /// without the leading '?'. Example: "a=1&amp;a=2&amp;b=".
        /// </summary>
        public static readonly HttpRequestOptionsKey<string> ParametersKey = new("arr.plugin.http.params");

        /// <summary>
        /// Stable, non-PII scope identifier (e.g., application-scoped user/account hash). Do not put raw tokens.
        /// </summary>
        public static readonly HttpRequestOptionsKey<string> AuthScopeKey = new("arr.plugin.http.auth-scope");
    }
}
