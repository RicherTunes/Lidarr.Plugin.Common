using System;
using System.Net;
using System.Net.Http;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;

namespace Examples
{
    public static class OAuthHttpClientExample
    {
        public static HttpClient Create(IStreamingTokenProvider tokenProvider, ILogger logger)
        {
            var handler = new OAuthDelegatingHandler(tokenProvider, logger)
            {
                InnerHandler = new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                }
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(100)
            };
        }
    }
}

