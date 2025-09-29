using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Extension methods for HttpClient to provide common functionality for streaming service plugins.
    /// </summary>
    public static class HttpClientExtensions
    {
        private sealed class HostGate
        {
            public HostGate(SemaphoreSlim semaphore, int limit)
            {
                Semaphore = semaphore;
                Limit = limit;
            }

            public SemaphoreSlim Semaphore { get; }

            public int Limit { get; private set; }

            public void UpdateLimit(int limit) => Limit = limit;
        }

        private static readonly ConcurrentDictionary<string, HostGate> _hostGates = new();

        private static SemaphoreSlim GetHostGate(string? host, int requestedLimit)
        {
            host ??= "__unknown__";
            return _hostGates.AddOrUpdate(
                host,
                _ => new HostGate(new SemaphoreSlim(requestedLimit, int.MaxValue), requestedLimit),
                (_, existing) =>
                {
                    if (requestedLimit > existing.Limit)
                    {
                        var delta = requestedLimit - existing.Limit;
                        existing.Semaphore.Release(delta);
                        existing.UpdateLimit(requestedLimit);
                    }

                    return existing;
                })
                .Semaphore;
        }
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

        /// <summary>
        /// Executes an HTTP request with enhanced resilience: 429/Retry-After awareness,
        /// exponential backoff with jitter, per-host concurrency gating, and retry budget.
        /// The provided request is cloned per attempt to allow safe retries.
        /// </summary>
        public static async Task<HttpResponseMessage> ExecuteWithResilienceAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            int maxRetries = 5,
            TimeSpan? retryBudget = null,
            int maxConcurrencyPerHost = 6,
            CancellationToken cancellationToken = default)
        {
            retryBudget ??= TimeSpan.FromSeconds(60);
            var deadline = DateTime.UtcNow + retryBudget.Value;
            var attempt = 0;
            var host = request.RequestUri?.Host;
            var gate = GetHostGate(host, Math.Max(1, maxConcurrencyPerHost));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    attempt++;

                    using var attemptRequest = await CloneHttpRequestMessageAsync(request).ConfigureAwait(false);
                    var response = await httpClient.SendAsync(attemptRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                    {
                        // Success; caller disposes response
                        return response;
                    }

                    // Determine if retryable
                    var status = (int)response.StatusCode;
                    var retryable = status == 408 || status == 429 || (status >= 500 && status <= 599);
                    if (!retryable || attempt >= maxRetries)
                    {
                        return response; // let caller handle non-retryable or last attempt
                    }

                    // Compute delay from headers or backoff
                    var delay = GetRetryDelay(response) ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))) + GetJitter();
                    response.Dispose(); // dispose before waiting

                    // Respect retry budget
                    var now = DateTime.UtcNow;
                    if (now + delay > deadline)
                    {
                        // Budget exceeded; stop retrying
                        break;
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }

            // Final attempt without retry or return last response -> do one last send
            using var finalRequest = await CloneHttpRequestMessageAsync(request).ConfigureAwait(false);
            return await httpClient.SendAsync(finalRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
#if NET8_0_OR_GREATER
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
#endif

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

        private static bool IsSensitiveParameter(string parameterName)
        {
            var lowerName = parameterName?.ToLowerInvariant() ?? string.Empty;
            return lowerName.Contains("token") ||
                   lowerName.Contains("secret") ||
                   lowerName.Contains("password") ||
                   lowerName.Contains("auth") ||
                   lowerName.Contains("credential") ||
                   lowerName.Contains("key") ||
                   lowerName == "request_sig" ||
                   lowerName == "sid" ||
                   lowerName.Contains("session") ||
                   lowerName.Contains("cookie") ||
                   lowerName.Contains("signature") ||
                   lowerName == "app_secret";
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

#if NET5_0_OR_GREATER
            clone.VersionPolicy = request.VersionPolicy;
            CopyHttpRequestOptions(request, clone);
#endif

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

#if NET5_0_OR_GREATER
        private static readonly MethodInfo HttpRequestOptionsSetMethod = typeof(HttpRequestOptions)
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
#endif
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
            var ms = Random.Shared.Next(50, 250);
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

