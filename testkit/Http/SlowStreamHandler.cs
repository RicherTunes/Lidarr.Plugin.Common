using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Streams data in fixed chunks with an artificial delay between reads to exercise timeout and cancellation paths.
/// </summary>
public sealed class SlowStreamHandler : HttpMessageHandler
{
    private readonly byte[] _payload;
    private readonly TimeSpan _delay;
    private readonly int _chunkSize;
    private readonly string _mediaType;

    public SlowStreamHandler(byte[] payload, TimeSpan? delayPerChunk = null, int chunkSize = 4096, string mediaType = "application/octet-stream")
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _delay = delayPerChunk ?? TimeSpan.FromMilliseconds(250);
        _chunkSize = chunkSize;
        _mediaType = mediaType;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var stream = new SlowStream(_payload, _delay, _chunkSize);
        var content = new StreamContent(stream, _chunkSize);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(_mediaType);
        content.Headers.ContentLength = _payload.LongLength;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };

        return Task.FromResult(response);
    }

    private sealed class SlowStream : Stream
    {
        private readonly byte[] _payload;
        private readonly TimeSpan _delay;
        private readonly int _chunkSize;
        private int _position;

        public SlowStream(byte[] payload, TimeSpan delay, int chunkSize)
        {
            _payload = payload;
            _delay = delay;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not supported; use ReadAsync.");

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= _payload.Length)
            {
                return 0;
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            var remaining = _payload.Length - _position;
            var toCopy = Math.Min(Math.Min(destination.Length, _chunkSize), remaining);
            _payload.AsSpan(_position, toCopy).CopyTo(destination.Span);
            _position += toCopy;
            return toCopy;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
