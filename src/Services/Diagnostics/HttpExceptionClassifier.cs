using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Diagnostics
{
    /// <summary>
    /// What kind of HTTP failure did we hit? Plugin <c>Test()</c> methods and
    /// connection-validation flows use this to pick an actionable error message
    /// instead of bubbling up CLR-flavoured exception text.
    /// </summary>
    public enum HttpFailureCategory
    {
        Unknown,
        Network,        // DNS, connection refused, network unreachable, transport-level IOException
        Timeout,        // Request never received a response within the window
        Cancelled,      // The caller's CancellationToken fired
        Auth,           // 401 / 403
        RateLimit,      // 429
        ClientRequest,  // 4xx other than auth / rate-limit (e.g. malformed query)
        Server          // 5xx
    }

    /// <summary>
    /// Categorised result from <see cref="HttpExceptionClassifier.Classify"/>.
    /// </summary>
    public readonly record struct HttpFailureClassification(HttpFailureCategory Category, string Hint);

    /// <summary>
    /// Maps exceptions thrown during HTTP calls into a categorical failure +
    /// user-readable hint. Plugin Test() methods historically caught generic
    /// <see cref="Exception"/> and surfaced "Test failed: {ex.Message}" which
    /// leaked CLR type names ("SocketException", "HttpRequestException") that
    /// aren't actionable for end users.
    ///
    /// This classifier replaces that pattern with:
    /// <code>
    /// catch (Exception ex)
    /// {
    ///     var (category, hint) = HttpExceptionClassifier.Classify(ex);
    ///     failures.Add(new ValidationFailure("Test", hint));
    /// }
    /// </code>
    ///
    /// Hints are intentionally short and free of CLR type names; they describe
    /// what the user can DO. Specifics (status code, URL) are out of scope —
    /// plugins should log those separately for operators.
    /// </summary>
    public static class HttpExceptionClassifier
    {
        public static HttpFailureClassification Classify(Exception exception)
        {
            if (exception is null)
            {
                return new HttpFailureClassification(HttpFailureCategory.Unknown, FallbackHint);
            }

            // Order matters — user-cancellation must win over the
            // TaskCanceledException-is-also-OperationCanceledException ambiguity.
            if (exception is OperationCanceledException oce && !(exception is TaskCanceledException))
            {
                return new HttpFailureClassification(HttpFailureCategory.Cancelled, "The test was cancelled.");
            }

            if (exception is TaskCanceledException)
            {
                // SendAsync timeout (no user cancellation observed at the call site).
                return new HttpFailureClassification(
                    HttpFailureCategory.Timeout,
                    "The request timed out before the server responded. The service may be slow or unreachable; try again.");
            }

            if (exception is HttpRequestException hre)
            {
                return ClassifyHttpRequest(hre);
            }

            if (exception is SocketException)
            {
                return new HttpFailureClassification(HttpFailureCategory.Network, NetworkHint);
            }

            if (exception is IOException)
            {
                // Connection-reset / read-failure mid-transfer typically surfaces
                // as IOException ("Unable to read data from the transport connection").
                return new HttpFailureClassification(HttpFailureCategory.Network, NetworkHint);
            }

            return new HttpFailureClassification(HttpFailureCategory.Unknown, FallbackHint);
        }

        private static HttpFailureClassification ClassifyHttpRequest(HttpRequestException hre)
        {
            if (hre.InnerException is SocketException || hre.InnerException is IOException)
            {
                return new HttpFailureClassification(HttpFailureCategory.Network, NetworkHint);
            }

            // .NET 5+ populates HttpRequestException.StatusCode on transport-success failures.
            // No status code typically means the request never reached a server.
            if (hre.StatusCode is null)
            {
                return new HttpFailureClassification(HttpFailureCategory.Network, NetworkHint);
            }

            var status = (int)hre.StatusCode.Value;
            return status switch
            {
                401 or 403 => new HttpFailureClassification(
                    HttpFailureCategory.Auth,
                    "The server rejected the credentials. Re-check the configured credentials and try again."),
                429 => new HttpFailureClassification(
                    HttpFailureCategory.RateLimit,
                    "The service is rate-limiting requests. Wait a few minutes and try again, or lower the requests-per-second setting."),
                >= 400 and < 500 => new HttpFailureClassification(
                    HttpFailureCategory.ClientRequest,
                    "The server rejected the request. Check the configured settings (e.g. region, market, search term) for an invalid value."),
                >= 500 => new HttpFailureClassification(
                    HttpFailureCategory.Server,
                    "The service returned a temporary server error. Try again in a few minutes."),
                _ => new HttpFailureClassification(HttpFailureCategory.Unknown, FallbackHint)
            };
        }

        private const string NetworkHint =
            "Could not reach the service over the network. Check connectivity, DNS, and any firewall or proxy between Lidarr and the service.";

        private const string FallbackHint =
            "An unexpected error occurred during the test. Check the Lidarr log for details.";
    }
}
