using System.Net;
using System.Text;
using NzbDrone.Common.Http;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Factory for creating <see cref="HttpResponse"/> objects in plugin unit tests.
/// Provides both auto-constructed (convenience) and explicit-request overloads.
/// </summary>
/// <remarks>
/// Consolidates three plugin-local copies:
/// <list type="bullet">
///   <item>Brainarr.TestKit.Providers.Http.HttpResponseFactory (21 LOC, explicit-request form)</item>
///   <item>Brainarr.Tests.Helpers.HttpResponseFactory (45 LOC, auto-constructs request)</item>
///   <item>Qobuzarr.Tests.Helpers.HttpTestHelpers (75 LOC, includes binary helper)</item>
/// </list>
/// </remarks>
public static class HttpResponseFactory
{
    private const string DefaultUrl = "http://test.local";

    // -------------------------------------------------------------------------
    // OK overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a 200 OK response with a JSON body.
    /// An ephemeral <c>http://test.local</c> request is constructed automatically.
    /// </summary>
    public static HttpResponse Ok(string json)
        => Ok(new HttpRequest(DefaultUrl), json);

    /// <summary>
    /// Creates a 200 OK response with a JSON body using the given request context.
    /// </summary>
    public static HttpResponse Ok(HttpRequest req, string json)
        => new(req, new HttpHeader(), Encoding.UTF8.GetBytes(json ?? string.Empty), HttpStatusCode.OK);

    /// <summary>
    /// Creates a 200 OK response with raw byte body using the given request context.
    /// </summary>
    public static HttpResponse Ok(HttpRequest req, byte[] bodyBytes)
        => new(req, new HttpHeader(), bodyBytes, HttpStatusCode.OK);

    // -------------------------------------------------------------------------
    // Error overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an error response with the given status code and optional text body.
    /// An ephemeral <c>http://test.local</c> request is constructed automatically.
    /// </summary>
    public static HttpResponse Error(HttpStatusCode status, string body = "")
        => Error(new HttpRequest(DefaultUrl), status, body);

    /// <summary>
    /// Creates an error response using the given request context.
    /// </summary>
    public static HttpResponse Error(HttpRequest req, HttpStatusCode status, string body = "")
        => new(req, new HttpHeader(), Encoding.UTF8.GetBytes(body ?? string.Empty), status);

    /// <summary>
    /// Creates an error response with raw byte body using the given request context.
    /// </summary>
    public static HttpResponse Error(HttpRequest req, HttpStatusCode status, byte[] bodyBytes)
        => new(req, new HttpHeader(), bodyBytes, status);

    // -------------------------------------------------------------------------
    // Binary helper (Qobuz pattern)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an HTTP response carrying binary data (e.g. audio chunks, downloads).
    /// An ephemeral <c>http://test.local</c> request is constructed automatically.
    /// </summary>
    /// <param name="data">Raw binary payload.</param>
    /// <param name="status">HTTP status code (defaults to <see cref="HttpStatusCode.OK"/>).</param>
    public static HttpResponse CreateBinaryResponse(byte[] data, HttpStatusCode status = HttpStatusCode.OK)
        => CreateBinaryResponse(new HttpRequest(DefaultUrl), data, status);

    /// <summary>
    /// Creates an HTTP response carrying binary data using the given request context.
    /// </summary>
    public static HttpResponse CreateBinaryResponse(HttpRequest req, byte[] data, HttpStatusCode status = HttpStatusCode.OK)
    {
        var headers = new HttpHeader();
        headers.ContentType = "application/octet-stream";
        return new HttpResponse(req, headers, data, status);
    }

    // -------------------------------------------------------------------------
    // Legacy compatibility aliases (Brainarr.Tests.Helpers.HttpResponseFactory shape)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Alias for <see cref="Ok(string)"/> — kept for call-site compatibility with
    /// the former Brainarr.Tests.Helpers.HttpResponseFactory.CreateResponse signature.
    /// </summary>
    public static HttpResponse CreateResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        => statusCode == HttpStatusCode.OK
            ? Ok(content)
            : Error(statusCode, content);

    /// <summary>
    /// Alias accepting an explicit request — kept for call-site compatibility.
    /// </summary>
    public static HttpResponse CreateResponse(HttpRequest request, string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        => statusCode == HttpStatusCode.OK
            ? Ok(request, content)
            : Error(request, statusCode, content);

    /// <summary>
    /// Alias for <see cref="Error(HttpStatusCode, string)"/> — kept for call-site
    /// compatibility with the former Qobuzarr.Tests.Helpers.HttpTestHelpers.CreateErrorResponse.
    /// </summary>
    public static HttpResponse CreateErrorResponse(HttpStatusCode statusCode, string errorContent = "Error")
        => Error(statusCode, errorContent);
}
