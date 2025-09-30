using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Returns a response flagged as a preview/sample stream using a configurable header.
/// </summary>
public sealed class PreviewStreamHandler : HttpMessageHandler
{
    private readonly byte[] _payload;
    private readonly string _mediaType;
    private readonly string _headerName;
    private readonly string _headerValue;
    private readonly HttpStatusCode _statusCode;

    public PreviewStreamHandler(string textPayload, string headerName = "X-Preview-Stream", string headerValue = "true", string mediaType = "audio/mpeg", HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _payload = Encoding.UTF8.GetBytes(textPayload);
        _mediaType = mediaType;
        _headerName = headerName;
        _headerValue = headerValue;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new ByteArrayContent(_payload)
        };

        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_mediaType);
        response.Headers.TryAddWithoutValidation(_headerName, _headerValue);
        return Task.FromResult(response);
    }
}
