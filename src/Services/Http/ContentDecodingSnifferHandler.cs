using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Safety net handler that inflates mislabelled gzip payloads where servers fail to emit Content-Encoding.
    /// </summary>
    public sealed class ContentDecodingSnifferHandler : DelegatingHandler
    {
        private const long MaxInspectionBytes = 1_048_576; // 1 MiB

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Content == null)
            {
                return response;
            }

            if (HasDeclaredEncoding(response.Content.Headers))
            {
                return response;
            }

            var declaredLength = GetDeclaredLength(response.Content);
            if (!declaredLength.HasValue || declaredLength.Value > MaxInspectionBytes)
            {
                // Unknown or large payloads are left untouched to avoid buffering unbounded data.
                return response;
            }

            var originalContent = response.Content;
            var originalHeaders = response.Content.Headers.Select(h => h).ToList();

            var payload = await HttpContentLightUp.ReadAsByteArrayAsync(originalContent, cancellationToken).ConfigureAwait(false);
            originalContent.Dispose();

            if (payload.Length == 0)
            {
                SetContent(response, Stream.Null, originalHeaders);
                return response;
            }

            if (!IsGzip(payload))
            {
                var passthrough = new MemoryStream(payload, writable: false);
                SetContent(response, passthrough, originalHeaders);
                return response;
            }

            using var bufferStream = new MemoryStream(payload, writable: false);
            using var gzip = new GZipStream(bufferStream, CompressionMode.Decompress, leaveOpen: true);
            var inflated = new MemoryStream();
            await gzip.CopyToAsync(inflated, cancellationToken).ConfigureAwait(false);
            inflated.Position = 0;

            SetContent(response, inflated, originalHeaders, treatAsJson: true);
            return response;
        }

        private static bool HasDeclaredEncoding(HttpContentHeaders headers)
        {
            return headers.ContentEncoding is { Count: > 0 };
        }

        private static long? GetDeclaredLength(HttpContent content)
        {
            return content.Headers.ContentLength;
        }

        private static bool IsGzip(byte[] payload)
        {
            return payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;
        }

        private static void SetContent(HttpResponseMessage response, Stream stream, List<KeyValuePair<string, IEnumerable<string>>> originalHeaders, bool treatAsJson = false)
        {
            response.Content?.Dispose();

            var newContent = stream == Stream.Null ? new StreamContent(Stream.Null) : new StreamContent(stream);
            foreach (var header in originalHeaders)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (treatAsJson && newContent.Headers.ContentType == null)
            {
                newContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            response.Content = newContent;
        }
    }
}

