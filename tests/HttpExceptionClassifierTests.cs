using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Diagnostics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Plugin Test() / connection-validation methods have historically caught any
/// Exception and surfaced "Test failed: {ex.Message}" to the user. That hides
/// the real failure category — a 401 looks like a 503 looks like a DNS error.
/// HttpExceptionClassifier converts an exception into a categorical
/// <see cref="HttpFailureCategory"/> plus a user-readable hint, so plugin UIs
/// can show actionable text like "Could not reach &lt;service&gt; — the server
/// returned a temporary error; try again in a few minutes." rather than
/// "Test failed: System.Net.Sockets.SocketException: No such host is known".
/// </summary>
public sealed class HttpExceptionClassifierTests
{
    [Fact]
    public void Classify_NullException_ReturnsUnknown()
    {
        // Defensive — a missing exception shouldn't crash the classifier.
        var result = HttpExceptionClassifier.Classify(null!);
        Assert.Equal(HttpFailureCategory.Unknown, result.Category);
    }

    [Fact]
    public void Classify_HttpRequestException_Unauthorized_ReturnsAuth()
    {
        var ex = new HttpRequestException("Unauthorized", inner: null, statusCode: HttpStatusCode.Unauthorized);
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Auth, result.Category);
        Assert.Contains("credentials", result.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_HttpRequestException_Forbidden_ReturnsAuth()
    {
        var ex = new HttpRequestException("Forbidden", inner: null, statusCode: HttpStatusCode.Forbidden);
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Auth, result.Category);
    }

    [Fact]
    public void Classify_HttpRequestException_TooManyRequests_ReturnsRateLimit()
    {
        var ex = new HttpRequestException("Too Many Requests", inner: null, statusCode: HttpStatusCode.TooManyRequests);
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.RateLimit, result.Category);
        Assert.Contains("rate", result.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_HttpRequestException_5xx_ReturnsServer()
    {
        foreach (var status in new[] {
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout })
        {
            var ex = new HttpRequestException("Boom", inner: null, statusCode: status);
            var result = HttpExceptionClassifier.Classify(ex);
            Assert.Equal(HttpFailureCategory.Server, result.Category);
        }
    }

    [Fact]
    public void Classify_HttpRequestException_NoStatusCode_FallsBackToNetworkOrServer()
    {
        // No status means we never got a response — typically a connection
        // failure. Route to Network, not Server.
        var ex = new HttpRequestException("Connection refused");
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Network, result.Category);
    }

    [Fact]
    public void Classify_TaskCanceledException_NoCancelToken_ReturnsTimeout()
    {
        // SendAsync timeout (no user cancellation) — should be classified as
        // a timeout, distinct from genuine user cancellation.
        var ex = new TaskCanceledException("A task was canceled.");
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Timeout, result.Category);
        Assert.Contains("timed out", result.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_OperationCanceledException_WithToken_ReturnsCancelled()
    {
        // Genuine user cancellation isn't really a "failure" — surface it as a
        // separate category so plugin UIs can suppress the test result rather
        // than showing an error.
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);

        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Cancelled, result.Category);
    }

    [Fact]
    public void Classify_SocketException_ReturnsNetwork()
    {
        var ex = new SocketException((int)SocketError.HostNotFound);
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Network, result.Category);
        Assert.Contains("network", result.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_HttpRequestExceptionWithSocketInner_ReturnsNetwork()
    {
        var inner = new SocketException((int)SocketError.NetworkUnreachable);
        var ex = new HttpRequestException("send failed", inner);

        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Network, result.Category);
    }

    [Fact]
    public void Classify_IOException_ReturnsNetwork()
    {
        // IO errors mid-transfer (connection reset, socket closed) — treat as
        // network rather than misleading the user with "auth" / "server".
        var ex = new IOException("Unable to read data from the transport connection");
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Network, result.Category);
    }

    [Fact]
    public void Classify_UnknownException_ReturnsUnknown()
    {
        var ex = new InvalidOperationException("something obscure");
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.Unknown, result.Category);
    }

    [Fact]
    public void Classify_HintNeverContainsRawCLRType()
    {
        // The hint is end-user-facing. It must not leak raw type names like
        // "System.Net.Http.HttpRequestException" or "System.Exception" — those
        // were the very strings the classifier exists to replace.
        var inputs = new Exception[]
        {
            new HttpRequestException("x", null, HttpStatusCode.Unauthorized),
            new HttpRequestException("x", null, HttpStatusCode.TooManyRequests),
            new HttpRequestException("x", null, HttpStatusCode.InternalServerError),
            new TaskCanceledException("timeout"),
            new SocketException(11001),
            new InvalidOperationException("opaque")
        };

        foreach (var ex in inputs)
        {
            var result = HttpExceptionClassifier.Classify(ex);
            Assert.False(result.Hint.Contains("System.", StringComparison.Ordinal),
                $"Hint leaked CLR type: '{result.Hint}'");
            Assert.False(result.Hint.Contains("Exception", StringComparison.Ordinal),
                $"Hint leaked 'Exception': '{result.Hint}'");
        }
    }

    [Fact]
    public void Classify_400_ReturnsClient()
    {
        // 4xx that isn't 401/403/429 — typically a malformed request (bad
        // query, missing required param). Distinct from auth so the user
        // can investigate their config rather than re-typing credentials.
        var ex = new HttpRequestException("Bad Request", inner: null, statusCode: HttpStatusCode.BadRequest);
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.ClientRequest, result.Category);
    }

    [Fact]
    public void Classify_404_ReturnsClient()
    {
        var ex = new HttpRequestException("Not Found", inner: null, statusCode: HttpStatusCode.NotFound);
        var result = HttpExceptionClassifier.Classify(ex);

        Assert.Equal(HttpFailureCategory.ClientRequest, result.Category);
    }
}
