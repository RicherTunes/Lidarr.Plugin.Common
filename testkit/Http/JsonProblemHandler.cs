using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Returns an <c>application/problem+json</c> payload to verify error deserialization logic.
/// </summary>
public sealed class JsonProblemHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _problemJson;

    public JsonProblemHandler(string title = "Upstream error", string detail = "Unexpected failure", HttpStatusCode statusCode = HttpStatusCode.BadRequest)
    {
        _statusCode = statusCode;
        _problemJson = $"{{\"title\":\"{title}\",\"detail\":\"{detail}\",\"status\":{(int)statusCode}}}";
    }

    public JsonProblemHandler(string rawJson, HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
        _problemJson = rawJson;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_problemJson, Encoding.UTF8)
        };

        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/problem+json");
        return Task.FromResult(response);
    }
}
