using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static partial class HttpContentLightUp
    {
        public static partial Task<string> ReadAsStringAsync(HttpContent content, CancellationToken cancellationToken);

        public static partial Task<byte[]> ReadAsByteArrayAsync(HttpContent content, CancellationToken cancellationToken);

        public static partial Task<Stream> ReadAsStreamAsync(HttpContent content, CancellationToken cancellationToken);
    }
}
