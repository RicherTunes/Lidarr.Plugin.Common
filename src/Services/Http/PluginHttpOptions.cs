using System.Net.Http;

namespace Lidarr.Plugin.Common.Services.Http
{
    public static class PluginHttpOptions
    {
        // Well-known request.Options keys for plugins to tag intent and caching policy.
        public static readonly HttpRequestOptionsKey<string> EndpointKey = new("lidarr.plugin.endpoint");
        public static readonly HttpRequestOptionsKey<string> ProfileKey = new("lidarr.plugin.profile");
        public static readonly HttpRequestOptionsKey<string> ParametersKey = new("lidarr.plugin.parameters");
        public static readonly HttpRequestOptionsKey<string> AuthScopeKey = new("lidarr.plugin.authscope");

        // Internal/implementation detail: buffers original request content exactly once for safe retries.
        internal static readonly HttpRequestOptionsKey<byte[]> BufferedBodyKey = new("lidarr.plugin._bufferedBody");
    }
}
