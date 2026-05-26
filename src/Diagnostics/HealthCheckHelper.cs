using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Diagnostics;
using Codes = Lidarr.Plugin.Common.Abstractions.Diagnostics.DiagnosticErrorCodes;

namespace Lidarr.Plugin.Common.Diagnostics;

/// <summary>
/// Shared scaffold for provider authentication health checks.
/// Wraps the Stopwatch+try/catch pattern that is repeated verbatim across
/// Tidalarr, Qobuzarr, and AppleMusicarr <c>CheckAuthAsync</c> implementations.
/// </summary>
/// <remarks>
/// Each plugin's local <c>*HealthDiagnostics</c> class retains its provider-specific
/// constants (provider name, authMethod, diagnosticType, capability, error messages)
/// and calls this helper instead of hand-rolling the timing scaffold.
/// </remarks>
public static class HealthCheckHelper
{
    /// <summary>
    /// Executes an authentication probe and wraps the result in a
    /// <see cref="DiagnosticHealthResult"/>.
    /// </summary>
    /// <param name="probe">
    /// Async function that performs the actual authentication check and returns
    /// <see langword="true"/> when the provider is authenticated and ready.
    /// The <see cref="CancellationToken"/> is forwarded from <paramref name="cancellationToken"/>.
    /// </param>
    /// <param name="provider">
    /// Provider identifier string (e.g. <c>"tidal"</c>, <c>"qobuz"</c>, <c>"apple-music"</c>).
    /// </param>
    /// <param name="authMethod">
    /// Authentication method string (e.g. <c>"oauth"</c>, <c>"app-secret"</c>,
    /// <c>"developer-token"</c>).
    /// </param>
    /// <param name="diagnosticType">
    /// Optional diagnostic type tag passed through to <see cref="DiagnosticHealthResult"/>.
    /// Defaults to <see langword="null"/>.
    /// </param>
    /// <param name="capability">
    /// Optional capability tag passed through to <see cref="DiagnosticHealthResult"/>.
    /// Defaults to <see langword="null"/>.
    /// </param>
    /// <param name="unhealthyMessage">
    /// Message used when <paramref name="probe"/> returns <see langword="false"/>.
    /// Defaults to <c>"Authentication failed"</c>.
    /// </param>
    /// <param name="authFailedErrorCode">
    /// Error code used when <paramref name="probe"/> returns <see langword="false"/>.
    /// Defaults to <see cref="DiagnosticErrorCodes.AuthFailed"/>.
    /// </param>
    /// <param name="connectionFailedErrorCode">
    /// Error code used when <paramref name="probe"/> throws an unexpected exception.
    /// Defaults to <see cref="DiagnosticErrorCodes.ConnectionFailed"/>.
    /// </param>
    /// <param name="exceptionMessageTransform">
    /// Optional delegate to format the exception message that appears in
    /// <see cref="DiagnosticHealthResult.StatusMessage"/> when the probe throws.
    /// When <see langword="null"/>, <see cref="Exception.Message"/> is used directly.
    /// Use this to prepend provider-specific actionable hints (e.g.
    /// "Check your Developer Token, Team ID, and Key ID…").
    /// </param>
    /// <param name="cancellationToken">Cancellation token forwarded to the probe.</param>
    /// <returns>
    /// A <see cref="DiagnosticHealthResult"/> that is healthy when the probe returns
    /// <see langword="true"/>, unhealthy with <paramref name="unhealthyMessage"/> when
    /// the probe returns <see langword="false"/>, and unhealthy with the exception message
    /// (optionally transformed) when the probe throws.
    /// </returns>
    public static async Task<DiagnosticHealthResult> CheckAuthAsync(
        Func<CancellationToken, Task<bool>> probe,
        string provider,
        string authMethod,
        string? diagnosticType = null,
        string? capability = null,
        string unhealthyMessage = "Authentication failed",
        string? authFailedErrorCode = null,
        string? connectionFailedErrorCode = null,
        Func<Exception, string>? exceptionMessageTransform = null,
        CancellationToken cancellationToken = default)
    {
        authFailedErrorCode ??= Codes.AuthFailed;
        connectionFailedErrorCode ??= Codes.ConnectionFailed;

        var sw = Stopwatch.StartNew();
        try
        {
            var ok = await probe(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            return ok
                ? DiagnosticHealthResult.Healthy(
                    responseTime: sw.Elapsed,
                    provider: provider,
                    authMethod: authMethod,
                    diagnosticType: diagnosticType,
                    capability: capability)
                : DiagnosticHealthResult.Unhealthy(
                    unhealthyMessage,
                    responseTime: sw.Elapsed,
                    provider: provider,
                    authMethod: authMethod,
                    diagnosticType: diagnosticType,
                    capability: capability,
                    errorCode: authFailedErrorCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var message = exceptionMessageTransform is not null
                ? exceptionMessageTransform(ex)
                : ex.Message;
            return DiagnosticHealthResult.Unhealthy(
                message,
                responseTime: sw.Elapsed,
                provider: provider,
                authMethod: authMethod,
                diagnosticType: diagnosticType,
                errorCode: connectionFailedErrorCode);
        }
    }
}
