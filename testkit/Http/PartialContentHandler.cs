using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Serves byte ranges to exercise resumable downloads and optional mid-stream failures.
/// </summary>
public sealed class PartialContentHandler : HttpMessageHandler
{
    private readonly byte[] _payload;
    private readonly string _mediaType;
    private readonly bool _throwOnFirstRange;
    private bool _hasThrown;

    public PartialContentHandler(byte[] payload, string mediaType = "application/octet-stream", bool throwOnFirstRange = false)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _mediaType = mediaType;
        _throwOnFirstRange = throwOnFirstRange;
    }

    public PartialContentHandler(string textPayload, string mediaType = "text/plain", bool throwOnFirstRange = false)
        : this(Encoding.UTF8.GetBytes(textPayload ?? throw new ArgumentNullException(nameof(textPayload))), mediaType, throwOnFirstRange)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_throwOnFirstRange && !_hasThrown && request.Headers.Range is not null)
        {
            _hasThrown = true;
            throw new HttpRequestException("Simulated transfer failure while serving range.");
        }

        if (request.Headers.Range is null || request.Headers.Range.Ranges.Count == 0)
        {
            return Task.FromResult(FullResponse());
        }

        var range = request.Headers.Range.Ranges.First();
        var from = range.From ?? 0;
        var to = range.To ?? (_payload.Length - 1);
        if (from >= _payload.Length)
        {
            var invalid = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };

            invalid.Content.Headers.ContentType = new MediaTypeHeaderValue(_mediaType);
            invalid.Content.Headers.ContentRange = new ContentRangeHeaderValue(_payload.Length);
            return Task.FromResult(invalid);
        }

        to = Math.Min(to, _payload.Length - 1);
        var length = (int)(to - from + 1);
        var slice = new byte[length];
        Array.Copy(_payload, (int)from, slice, 0, length);

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue(_mediaType);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _payload.Length);

        return Task.FromResult(response);
    }

    private HttpResponseMessage FullResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_payload)
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue(_mediaType);
        response.Content.Headers.ContentLength = _payload.Length;
        return response;
    }
}

