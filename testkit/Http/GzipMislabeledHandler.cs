using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Returns a gzipped payload without setting the <c>Content-Encoding</c> header.
/// Useful for exercising sniffers that must detect compression manually.
/// </summary>
public sealed class GzipMislabeledHandler : HttpMessageHandler
{
    private readonly byte[] _gzipPayload;
    private readonly HttpStatusCode _statusCode;
    private readonly string _mediaType;

    public GzipMislabeledHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK, string mediaType = "application/json")
    {
        _gzipPayload = Encode(json);
        _statusCode = statusCode;
        _mediaType = mediaType;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(_gzipPayload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(_mediaType);

        var response = new HttpResponseMessage(_statusCode)
        {
            Content = content
        };

        return Task.FromResult(response);
    }

    private static byte[] Encode(string value)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8, 1024, leaveOpen: true))
        {
            writer.Write(value);
        }

        return buffer.ToArray();
    }
}
