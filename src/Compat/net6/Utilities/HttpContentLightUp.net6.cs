using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static partial class HttpContentLightUp
    {
        public static async partial Task<string> ReadAsStringAsync(HttpContent content, CancellationToken cancellationToken)
        {
            var payload = await content.ReadAsStringAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return payload;
        }

        public static async partial Task<byte[]> ReadAsByteArrayAsync(HttpContent content, CancellationToken cancellationToken)
        {
            var bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }

        public static async partial Task<Stream> ReadAsStreamAsync(HttpContent content, CancellationToken cancellationToken)
        {
            var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return stream;
        }
    }
}
