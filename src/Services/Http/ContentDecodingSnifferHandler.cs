using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Safety net handler that inflates mislabelled gzip payloads where servers fail to emit Content-Encoding.
    /// </summary>
    public sealed class ContentDecodingSnifferHandler : DelegatingHandler
    {
        private const int GzipHeaderLength = 2;

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

            var originalContent = response.Content;
            var originalHeaders = originalContent.Headers
                .Select(static h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value))
                .ToList();

            var stream = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var prefixBuffer = new byte[GzipHeaderLength];
            var bytesRead = await ReadPrefixAsync(stream, prefixBuffer, cancellationToken).ConfigureAwait(false);

            if (bytesRead < GzipHeaderLength)
            {
                var passthrough = new PrefixStream(prefixBuffer, bytesRead, stream);
                SetContent(response, passthrough, originalHeaders);
                return response;
            }

            if (!IsGzip(prefixBuffer.AsSpan(0, bytesRead)))
            {
                var passthrough = new PrefixStream(prefixBuffer, bytesRead, stream);
                SetContent(response, passthrough, originalHeaders);
                return response;
            }

            var concatenated = new PrefixStream(prefixBuffer, bytesRead, stream);
            var gzipStream = new GZipStream(concatenated, CompressionMode.Decompress);
            SetContent(response, gzipStream, originalHeaders, treatAsJson: true, preserveContentLength: false);
            return response;
        }

        private static async Task<int> ReadPrefixAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static bool HasDeclaredEncoding(HttpContentHeaders headers)
        {
            return headers.ContentEncoding is { Count: > 0 };
        }

        private static bool IsGzip(ReadOnlySpan<byte> header)
        {
            return header.Length >= GzipHeaderLength && header[0] == 0x1F && header[1] == 0x8B;
        }

        private static void SetContent(
            HttpResponseMessage response,
            Stream stream,
            List<KeyValuePair<string, IEnumerable<string>>> originalHeaders,
            bool treatAsJson = false,
            bool preserveContentLength = true)
        {
            var newContent = new StreamContent(stream ?? Stream.Null);

            foreach (var header in originalHeaders)
            {
                if (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!preserveContentLength && header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
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

        private sealed class PrefixStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly int _prefixLength;
            private int _position;
            private readonly Stream _inner;

            public PrefixStream(byte[] prefix, int prefixLength, Stream inner)
            {
                _prefix = prefix ?? Array.Empty<byte>();
                _prefixLength = Math.Min(_prefix.Length, Math.Max(prefixLength, 0));
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                ValidateReadArgs(buffer, offset, count);

                var read = 0;
                if (_position < _prefixLength)
                {
                    var toCopy = Math.Min(count, _prefixLength - _position);
                    Array.Copy(_prefix, _position, buffer, offset, toCopy);
                    _position += toCopy;
                    offset += toCopy;
                    count -= toCopy;
                    read += toCopy;
                }

                if (count > 0)
                {
                    read += _inner.Read(buffer, offset, count);
                }

                return read;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ValidateReadArgs(buffer, offset, count);

                var read = 0;
                if (_position < _prefixLength)
                {
                    var toCopy = Math.Min(count, _prefixLength - _position);
                    Array.Copy(_prefix, _position, buffer, offset, toCopy);
                    _position += toCopy;
                    offset += toCopy;
                    count -= toCopy;
                    read += toCopy;

                    if (count == 0)
                    {
                        return read;
                    }
                }

                var innerRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                return read + innerRead;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            private static void ValidateReadArgs(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (buffer.Length - offset < count) throw new ArgumentException("Invalid offset/count combination", nameof(count));
            }
        }
    }
}
