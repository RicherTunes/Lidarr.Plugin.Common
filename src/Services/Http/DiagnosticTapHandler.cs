using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Minimal diagnostics wire-tap for HTTP traffic. When enabled via environment variable
    /// LIDARR_PLUGIN_HTTP_TAP ("1"/"true"), logs request/response lines and basic headers.
    /// - Redacts sensitive headers and query parameters.
    /// - Avoids mutating the pipeline; only mirrors small textual responses for logging.
    /// </summary>
    public sealed class DiagnosticTapHandler : DelegatingHandler
    {
        private const string EnvFlag = "LIDARR_PLUGIN_HTTP_TAP";
        private const int MaxLoggedBodyBytes = 1024 * 2; // 2 KiB

        private readonly ILogger? _logger;
        private readonly bool _enabled;

        public DiagnosticTapHandler(ILogger<DiagnosticTapHandler>? logger = null)
        {
            _logger = logger;
            _enabled = IsEnabled();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!_enabled || _logger is null || !_logger.IsEnabled(LogLevel.Information))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var sw = ValueStopwatch.StartNew();
            var id = CreateId();
            try
            {
                var uriForLog = SanitizeUri(request.RequestUri);
                _logger.LogInformation("http[{Id}] -> {Method} {Uri}", id, request.Method.Method, uriForLog);
                LogHeaders(id, request.Headers, isRequest: true);
                if (request.Content != null)
                {
                    LogHeaders(id, request.Content.Headers, isRequest: true);
                    // Intentionally skip request body to avoid consuming non-buffered content.
                }

                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                var elapsedMs = sw.GetElapsedMilliseconds();
                _logger.LogInformation("http[{Id}] <- {Status} {Reason} ({Elapsed} ms)", id, (int)response.StatusCode, response.ReasonPhrase, elapsedMs);
                LogHeaders(id, response.Headers, isRequest: false);
                if (response.Content != null)
                {
                    LogHeaders(id, response.Content.Headers, isRequest: false);
                    if (IsTextual(response.Content.Headers))
                    {
                        try
                        {
                            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                            var toLog = bytes.Length > MaxLoggedBodyBytes ? bytes.AsSpan(0, MaxLoggedBodyBytes).ToArray() : bytes;
                            var encoding = TryGetEncoding(response.Content.Headers?.ContentType?.CharSet) ?? Encoding.UTF8;
                            var snippet = SafeGetString(encoding, toLog);
                            _logger.LogDebug("http[{Id}] body: {Snippet}{Trunc}", id, snippet, bytes.Length > MaxLoggedBodyBytes ? " [truncated]" : string.Empty);

                            // Rebuild content so downstream can still read it.
                            var clone = new ByteArrayContent(bytes);
                            CopyHeaders(response.Content.Headers, clone.Headers);
                            response.Content = clone;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "http[{Id}] failed to mirror response body for logging.", id);
                        }
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "http[{Id}] exception during send.", id);
                throw;
            }
        }

        private static bool IsEnabled()
        {
            var value = Environment.GetEnvironmentVariable(EnvFlag);
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateId()
        {
            // 6-char base36 id for compact correlation
            var bytes = Guid.NewGuid().ToByteArray();
            var u = BitConverter.ToUInt32(bytes, 0);
            var chars = "0123456789abcdefghijklmnopqrstuvwxyz";
            var buf = new char[6];
            for (int i = 0; i < buf.Length; i++) { buf[i] = chars[(int)(u % 36)]; u /= 36; }
            Array.Reverse(buf);
            return new string(buf);
        }

        private void LogHeaders<T>(string id, T headers, bool isRequest) where T : class
        {
            if (headers is null) return;
            if (headers is HttpHeaders hh)
            {
                foreach (var kv in hh)
                {
                    if (IsSensitiveHeader(kv.Key)) continue;
                    foreach (var v in kv.Value)
                    {
                        _logger!.LogTrace("http[{Id}] {Dir} hdr {Key}: {Value}", id, isRequest ? "req" : "rsp", kv.Key, v);
                    }
                }
            }
        }

        private static string SanitizeUri(Uri? uri)
        {
            if (uri == null) return string.Empty;
            var s = uri.ToString();
            if (string.IsNullOrEmpty(uri.Query)) return s;

            try
            {
                var queryPart = uri.Query.TrimStart('?');
                var pairs = queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries);
                var rebuilt = new StringBuilder();
                for (int i = 0; i < pairs.Length; i++)
                {
                    var kv = pairs[i];
                    var idx = kv.IndexOf('=');
                    string key, val;
                    if (idx >= 0)
                    {
                        key = kv.Substring(0, idx);
                        val = kv.Substring(idx + 1);
                    }
                    else
                    {
                        key = kv;
                        val = string.Empty;
                    }

                    if (IsSensitiveKey(Uri.UnescapeDataString(key)))
                    {
                        val = "REDACTED";
                    }

                    if (rebuilt.Length > 0) rebuilt.Append('&');
                    rebuilt.Append(key);
                    if (idx >= 0) { rebuilt.Append('='); rebuilt.Append(val); }
                }

                var ub = new UriBuilder(uri) { Query = rebuilt.ToString() };
                return ub.Uri.ToString();
            }
            catch { return s; }
        }

        private static bool IsSensitiveHeader(string name)
            => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-Signature", StringComparison.OrdinalIgnoreCase);

        private static bool IsSensitiveKey(string key)
            => key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("code", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase);

        private static bool IsTextual(HttpContentHeaders? headers)
        {
            var mt = headers?.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mt)) return false;
            if (mt.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mt.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return true;
            if (mt.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void CopyHeaders(HttpContentHeaders from, HttpContentHeaders to)
        {
            foreach (var h in from)
            {
                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                to.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        private static Encoding? TryGetEncoding(string? charset)
        {
            if (string.IsNullOrWhiteSpace(charset)) return null;
            try { return Encoding.GetEncoding(charset); }
            catch { return null; }
        }

        private static string SafeGetString(Encoding encoding, byte[] bytes)
        {
            try { return encoding.GetString(bytes); }
            catch { return Convert.ToBase64String(bytes); }
        }

        private readonly struct ValueStopwatch
        {
            private readonly long _start;

            private ValueStopwatch(long start) { _start = start; }
            public static ValueStopwatch StartNew() => new ValueStopwatch(Stopwatch.GetTimestamp());
            public long GetElapsedMilliseconds()
            {
                var end = Stopwatch.GetTimestamp();
                return (long)((end - _start) * 1000.0 / Stopwatch.Frequency);
            }
        }
    }
}
