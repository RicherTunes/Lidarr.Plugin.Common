using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Lidarr.Plugin.Common.Services.Deduplication;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Extension methods for HttpClient to provide common functionality for streaming service plugins.
    /// </summary>
    public static class HttpClientExtensions
    {

        /// <summary>
        /// Executes an HTTP request with built-in retry logic and error handling.
        /// </summary>
        public static async Task<HttpResponseMessage> ExecuteWithRetryAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries = 3,
            int initialDelayMs = 1000,
            CancellationToken cancellationToken = default)
        {
            return await RetryUtilities.ExecuteWithRetryAsync(
                async () =>
                {
                    // Clone the request for retry attempts
                    var clonedRequest = await CloneHttpRequestMessageAsync(request);
                    return await httpClient.SendAsync(clonedRequest, cancellationToken);
                },
                maxRetries,
                initialDelayMs,
                $"HTTP {request.Method} to {request.RequestUri}");
        }

        public static Task<HttpResponseMessage> ExecuteWithResilienceAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            return ExecuteWithResilienceAsyncCore(
                httpClient,
                request,
                maxRetries,
                retryBudget,
                maxConcurrencyPerHost,
                perRequestTimeout,
                cancellationToken);
        }

        /// <summary>
        /// Overload that accepts a ResiliencePolicy and maps it to the core implementation.
        /// Keeps call sites minimal and avoids argument order mistakes.
        /// </summary>
        public static Task<HttpResponseMessage> ExecuteWithResilienceAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            ResiliencePolicy policy,
            CancellationToken cancellationToken = default)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return ExecuteWithResilienceAsyncCore(
                httpClient,
                request,
                policy.MaxRetries,
                policy.RetryBudget,
                policy.MaxConcurrencyPerHost,
                policy.PerRequestTimeout,
                cancellationToken);
        }


        public static async Task<HttpResponseMessage> SendWithResilienceAsync(
            this HttpClient httpClient,
            Services.Http.StreamingApiRequestBuilder builder,
            int maxRetries = 5,
            TimeSpan? retryBudget = null,
            int maxConcurrencyPerHost = 6,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            var request = builder.Build();
            var info = builder.BuildForLogging();

            try
            {
                // Prefer policy-based execution when the builder specifies it.
                if (builder.Policy != null)
                {
                    return await httpClient.ExecuteWithResilienceAsync(
                        request,
                        builder.Policy,
                        cancellationToken).ConfigureAwait(false);
                }

                return await httpClient.ExecuteWithResilienceAsync(
                    request,
                    maxRetries,
                    retryBudget,
                    maxConcurrencyPerHost,
                    info.Timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Sends a request with resilience and singleflight deduplication for identical GETs.
        /// Deduplication shares a buffered HTTP record (status, headers-of-interest, content bytes) across callers,
        /// and rebuilds a fresh HttpResponseMessage per consumer to avoid shared disposal issues.
        /// </summary>
        public static async Task<HttpResponseMessage> SendWithResilienceAsync(
            this HttpClient httpClient,
            Services.Http.StreamingApiRequestBuilder builder,
            RequestDeduplicator deduplicator,
            int maxRetries = 5,
            TimeSpan? retryBudget = null,
            int maxConcurrencyPerHost = 6,
            CancellationToken cancellationToken = default)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (deduplicator == null) throw new ArgumentNullException(nameof(deduplicator));

            var request = builder.Build();
            var info = builder.BuildForLogging();

            try
            {
                var method = request.Method?.Method?.ToUpperInvariant() ?? "GET";
                // Only dedupe GET-like requests
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    // fall back to normal path
                    if (builder.Policy != null)
                    {
                        return await httpClient.ExecuteWithResilienceAsync(request, builder.Policy, cancellationToken).ConfigureAwait(false);
                    }

                    return await httpClient.ExecuteWithResilienceAsync(
                        request,
                        maxRetries,
                        retryBudget,
                        maxConcurrencyPerHost,
                        info.Timeout,
                        cancellationToken).ConfigureAwait(false);
                }

                var key = BuildRequestDedupKey(request);
                var record = await deduplicator.GetOrCreateAsync(key, async () =>
                {
                    // Send once and buffer response into a small immutable record
                    using var resp = builder.Policy != null
                        ? await httpClient.ExecuteWithResilienceAsync(request, builder.Policy, cancellationToken).ConfigureAwait(false)
                        : await httpClient.ExecuteWithResilienceAsync(request, maxRetries, retryBudget, maxConcurrencyPerHost, info.Timeout, cancellationToken).ConfigureAwait(false);

                    var status = (int)resp.StatusCode;
                    var reason = resp.ReasonPhrase;

                    // Snapshot response headers of interest
                    var headerPairs = resp.Headers?.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToArray() ?? Array.Empty<KeyValuePair<string, string>>();
                    var contentHeaderPairs = resp.Content?.Headers?.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToArray() ?? Array.Empty<KeyValuePair<string, string>>();

                    var bytes = resp.Content != null ? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false) : Array.Empty<byte>();
                    var mediaType = resp.Content?.Headers?.ContentType?.MediaType;
                    var encoding = resp.Content?.Headers?.ContentType?.CharSet;

                    return new HttpRecord(status, reason, headerPairs, contentHeaderPairs, bytes, mediaType, encoding);
                }, cancellationToken).ConfigureAwait(false);

                // Rebuild a fresh HttpResponseMessage per consumer
                var rebuilt = new HttpResponseMessage((HttpStatusCode)record.StatusCode)
                {
                    ReasonPhrase = record.ReasonPhrase,
                    Content = new ByteArrayContent(record.ContentBytes)
                };

                foreach (var h in record.Headers)
                {
                    rebuilt.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                foreach (var ch in record.ContentHeaders)
                {
                    rebuilt.Content.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
                }

                if (!string.IsNullOrWhiteSpace(record.MediaType))
                {
                    rebuilt.Content.Headers.ContentType = new MediaTypeHeaderValue(record.MediaType);
                    if (!string.IsNullOrWhiteSpace(record.Charset))
                    {
                        rebuilt.Content.Headers.ContentType.CharSet = record.Charset;
                    }
                }

                return rebuilt;
            }
            finally
            {
                request.Dispose();
            }
        }

        private readonly record struct HttpRecord(
            int StatusCode,
            string? ReasonPhrase,
            KeyValuePair<string, string>[] Headers,
            KeyValuePair<string, string>[] ContentHeaders,
            byte[] ContentBytes,
            string? MediaType,
            string? Charset);
        private static async Task<HttpResponseMessage> ExecuteWithResilienceAsyncCore(
            HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries,
            TimeSpan? retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            CancellationToken cancellationToken)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (request == null) throw new ArgumentNullException(nameof(request));

            retryBudget ??= TimeSpan.FromSeconds(60);
            using var timeoutCts = perRequestTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (perRequestTimeout.HasValue)
            {
                timeoutCts!.CancelAfter(perRequestTimeout.Value);
            }

            var effectiveToken = timeoutCts?.Token ?? cancellationToken;
            var deadline = DateTime.UtcNow + retryBudget.Value;
            var attempt = 0;
            var host = request.RequestUri?.Host;
            string hostKey = host ?? "__unknown__";
            try
            {
                if (request.Options.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ProfileKey, out string? profile) && !string.IsNullOrWhiteSpace(profile))
                {
                    hostKey = hostKey + "|" + profile;
                }
            }
            catch { }

            var gate = HostGateRegistry.Get(hostKey, Math.Max(1, maxConcurrencyPerHost));

            await gate.WaitAsync(effectiveToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    attempt++;

                    using var attemptRequest = await CloneHttpRequestMessageAsync(request).ConfigureAwait(false);

                    HttpResponseMessage response;
                    try
                    {
                        response = await httpClient.SendAsync(
                                attemptRequest,
                                HttpCompletionOption.ResponseHeadersRead,
                                effectiveToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (perRequestTimeout.HasValue &&
                                                               timeoutCts!.IsCancellationRequested &&
                                                               !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"HTTP request to {request.RequestUri} exceeded the per-request timeout of {perRequestTimeout.Value}.",
                            ex);
                    }

                    if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                    {
                        return response;
                    }

                    var status = (int)response.StatusCode;
                    var retryable = status == 408 || status == 429 || (status >= 500 && status <= 599);
                    if (!retryable || attempt >= maxRetries)
                    {
                        return response;
                    }

                    var delay = GetRetryDelay(response)
                               ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + GetJitter();

                    var now = DateTime.UtcNow;
                    if (now + delay > deadline)
                    {
                        return response;
                    }

                    response.Dispose();
                    await Task.Delay(delay, effectiveToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Executes an HTTP request and deserializes the JSON response.
        /// </summary>
        public static async Task<T> GetJsonAsync<T>(
            this HttpClient httpClient,
            string url,
            JsonSerializerOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var payload = await HttpContentLightUp.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return default;
            }

            if (!IsJsonMediaType(contentType))
            {
                var previewLength = Math.Min(8, payload.Length);
                var previewSegment = payload.Substring(0, previewLength);
                var previewBytes = Encoding.UTF8.GetBytes(previewSegment);
                var previewHex = previewLength > 0 ? BitConverter.ToString(previewBytes) : "<empty>";
                var statusCode = (int)response.StatusCode;
                throw new InvalidOperationException($"Expected JSON but got '{contentType ?? "none"}' from {response.RequestMessage?.RequestUri} (status {statusCode}) (first bytes: {previewHex}).");
            }

            options ??= new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var result = JsonSerializer.Deserialize<T>(payload, options);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to deserialize JSON payload into the requested type.");
            }

            return result;
        }

        /// <summary>
        /// Posts JSON data and returns a deserialized response.
        /// </summary>
        public static async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            this HttpClient httpClient,
            string url,
            TRequest data,
            JsonSerializerOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(data, options);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TResponse>(responseContent, options);
        }

        /// <summary>
        /// Builds a URL with query parameters from a dictionary.
        /// </summary>
        public static string BuildUrlWithParams(string baseUrl, Dictionary<string, string> parameters)
        {
            if (parameters == null || !parameters.Any())
                return baseUrl;

            var queryString = string.Join("&",
                parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

            var separator = baseUrl.Contains('?') ? "&" : "?";
            return $"{baseUrl}{separator}{queryString}";
        }

        /// <summary>
        /// Builds a URL with query parameters from a sequence of pairs (supports multivalue keys).
        /// </summary>
        public static string BuildUrlWithParams(string baseUrl, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            if (parameters == null)
                return baseUrl;

            var list = parameters as IList<KeyValuePair<string, string>> ?? parameters.ToList();
            if (list.Count == 0) return baseUrl;

            var queryString = string.Join("&",
                list.Select(p => $"{Uri.EscapeDataString(p.Key ?? string.Empty)}={Uri.EscapeDataString(p.Value ?? string.Empty)}"));

            var separator = baseUrl.Contains('?') ? "&" : "?";
            return $"{baseUrl}{separator}{queryString}";
        }

        /// <summary>
        /// Adds standard headers for streaming service API calls.
        /// </summary>
        public static HttpRequestMessage AddStandardHeaders(
            this HttpRequestMessage request,
            string userAgent = null,
            Dictionary<string, string> additionalHeaders = null)
        {
            if (!string.IsNullOrEmpty(userAgent))
            {
                request.Headers.Add("User-Agent", userAgent);
            }

            // Common headers for streaming APIs
            request.Headers.Add("Accept", "application/json");
            // Do not set Accept-Encoding here; rely on the HTTP handler configuration.
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }

            return request;
        }

        /// <summary>
        /// Measures execution time of an HTTP request.
        /// </summary>
        public static async Task<(HttpResponseMessage Response, TimeSpan Duration)> ExecuteWithTimingAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await httpClient.SendAsync(request, cancellationToken);
                stopwatch.Stop();
                return (response, stopwatch.Elapsed);
            }
            catch
            {
                stopwatch.Stop();
                throw;
            }
        }

        /// <summary>
        /// Safe method to read response content with encoding detection.
        /// </summary>
        public static async Task<string> ReadContentSafelyAsync(this HttpContent content)
        {
            try
            {
                return await content.ReadAsStringAsync();
            }
            catch (Exception)
            {
                // Fallback to byte reading if string reading fails
                var bytes = await content.ReadAsByteArrayAsync();
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary>
        /// Validates if the response content type is JSON.
        /// </summary>
        public static bool IsJsonContent(this HttpResponseMessage response)
        {
            var contentType = response.Content?.Headers?.ContentType?.MediaType;
            return IsJsonMediaType(contentType);
        }

        /// <summary>
        /// Creates a safe copy of sensitive parameters for logging.
        /// </summary>
        public static Dictionary<string, string> MaskSensitiveParams(Dictionary<string, string> parameters)
        {
            if (parameters == null) return new Dictionary<string, string>();

            var maskedParams = new Dictionary<string, string>();
            foreach (var param in parameters)
            {
                if (IsSensitiveParameter(param.Key))
                {
                    maskedParams[param.Key] = MaskValue(param.Value);
                }
                else
                {
                    maskedParams[param.Key] = param.Value;
                }
            }
            return maskedParams;
        }

        /// <summary>
        /// Builds a stable dedup key for GET requests based on request metadata.
        /// Includes authority, endpoint, canonical query parameters, optional auth scope, and profile.
        /// </summary>
        public static string BuildRequestDedupKey(HttpRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var method = request.Method?.Method?.ToUpperInvariant() ?? "GET";
            var authority = request.RequestUri != null
                ? ($"{(request.RequestUri.Scheme ?? "http").ToLowerInvariant()}://{request.RequestUri.Host.ToLowerInvariant()}{(request.RequestUri.IsDefaultPort ? string.Empty : ":" + request.RequestUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}")
                : "";

            string endpoint = string.Empty;
            string profile = string.Empty;
            string canonical = string.Empty;
            string scope = string.Empty;

            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.EndpointKey, out string? ep) && ep != null)
                    endpoint = ep;
            }
            catch { }
            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.ProfileKey, out string? pr) && pr != null)
                    profile = pr;
            }
            catch { }
            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.ParametersKey, out string? ca) && ca != null)
                    canonical = ca;
            }
            catch { }
            try
            {
                if (request.Options.TryGetValue(Services.Http.PluginHttpOptions.AuthScopeKey, out string? sc) && sc != null)
                    scope = sc;
            }
            catch { }

            var parts = new[] { method, authority, endpoint, canonical, scope, profile };
            return HashingUtility.ComputeSHA256(string.Join("\n", parts));
        }

        private static bool IsSensitiveParameter(string parameterName)
        {
            return SensitiveKeys.IsSensitive(parameterName);
        }
        private static string MaskValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "[empty]";

            if (value.Length <= 4)
                return new string('*', value.Length);

            return $"{value.Substring(0, 2)}{"*".PadLeft(value.Length - 4, '*')}{value.Substring(value.Length - 2)}";
        }

        public static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version
            };

            clone.VersionPolicy = request.VersionPolicy;
            CopyHttpRequestOptions(request, clone);
            // Copy headers
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Copy content if present
            if (request.Content != null)
            {
                var contentBytes = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(contentBytes);

                // Copy content headers
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        private static readonly MethodInfo HttpRequestOptionsSetMethod = typeof(System.Net.Http.HttpRequestOptions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);

        private static void CopyHttpRequestOptions(HttpRequestMessage source, HttpRequestMessage destination)
        {
            foreach (var option in source.Options)
            {
                var value = option.Value;
                if (value is null)
                {
                    destination.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), null);
                    continue;
                }

                var valueType = value.GetType();
                var keyType = typeof(HttpRequestOptionsKey<>).MakeGenericType(valueType);
                var keyInstance = Activator.CreateInstance(keyType, option.Key);
                var setMethod = HttpRequestOptionsSetMethod.MakeGenericMethod(valueType);
                setMethod.Invoke(destination.Options, new[] { keyInstance, value });
            }
        }
        private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
        {
            try
            {
                var ra = response.Headers?.RetryAfter;
                if (ra == null) return null;

                if (ra.Delta.HasValue) return ra.Delta.Value;
                if (ra.Date.HasValue)
                {
                    var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                    if (delta > TimeSpan.Zero) return delta;
                }
            }
            catch { /* ignore parse issues */ }
            return null;
        }

        private static TimeSpan GetJitter()
        {
            var ms = RandomProvider.Next(50, 250);
            return TimeSpan.FromMilliseconds(ms);
        }

        private static bool IsJsonMediaType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            return contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("text/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase);
        }
    }
}










