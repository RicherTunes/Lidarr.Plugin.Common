using System.Net;
using System.Net.Http;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static partial class HttpHandlerFactory
    {
        public static partial HttpMessageHandler CreateDefaultHandler()
            => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };
    }
}
