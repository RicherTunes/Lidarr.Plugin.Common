using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Builder for creating HTTP requests specific to streaming service APIs.
    /// Provides a fluent interface for common streaming service request patterns.
    /// </summary>
    public class StreamingApiRequestBuilder
    {
        private readonly string _baseUrl;
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Preserve multivalue query semantics by storing pairs instead of a map.
        private readonly List<KeyValuePair<string, string>> _queryParams = new List<KeyValuePair<string, string>>();
        private HttpMethod _method = HttpMethod.Get;
        private object _bodyContent;
        private string _endpoint;
        private TimeSpan? _timeout;
        private ResiliencePolicy _policy;
        private string _authScopeToken;

        public StreamingApiRequestBuilder(string baseUrl)
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        /// <summary>
        /// Sets the API endpoint path.
        /// </summary>
        public StreamingApiRequestBuilder Endpoint(string endpoint)
        {
            _endpoint = endpoint?.TrimStart('/');
            return this;
        }

        /// <summary>
        /// Sets the HTTP method.
        /// </summary>
        public StreamingApiRequestBuilder Method(HttpMethod method)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            return this;
        }

        /// <summary>
        /// Convenience method for GET requests.
        /// </summary>
        public StreamingApiRequestBuilder Get() => Method(HttpMethod.Get);

        /// <summary>
        /// Convenience method for POST requests.
        /// </summary>
        public StreamingApiRequestBuilder Post() => Method(HttpMethod.Post);

        /// <summary>
        /// Convenience method for PUT requests.
        /// </summary>
        public StreamingApiRequestBuilder Put() => Method(HttpMethod.Put);

        /// <summary>
        /// Convenience method for DELETE requests.
        /// </summary>
        public StreamingApiRequestBuilder Delete() => Method(HttpMethod.Delete);

        /// <summary>
        /// Adds an authorization header with Bearer token.
        /// </summary>
        public StreamingApiRequestBuilder BearerToken(string token)
        {
            if (!string.IsNullOrEmpty(token))
            {
                _headers["Authorization"] = $"Bearer {token}";
            }
            return this;
        }

        /// <summary>
        /// Adds an API key header.
        /// </summary>
        public StreamingApiRequestBuilder ApiKey(string headerName, string apiKey)
        {
            if (!string.IsNullOrEmpty(headerName) && !string.IsNullOrEmpty(apiKey))
            {
                _headers[headerName] = apiKey;
            }
            return this;
        }

        /// <summary>
        /// Adds a custom header.
        /// </summary>
        public StreamingApiRequestBuilder Header(string name, string value)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
            {
                _headers[name] = value;
            }
            return this;
        }

        /// <summary>
        /// Adds multiple headers from a dictionary.
        /// </summary>
        public StreamingApiRequestBuilder Headers(Dictionary<string, string> headers)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    _headers[header.Key] = header.Value;
                }
            }
            return this;
        }

        /// <summary>
        /// Adds a query parameter.
        /// </summary>
        public StreamingApiRequestBuilder Query(string name, string value)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _queryParams.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            }
            return this;
        }

        /// <summary>
        /// Adds a query parameter with integer value.
        /// </summary>
        public StreamingApiRequestBuilder Query(string name, int value)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _queryParams.Add(new KeyValuePair<string, string>(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            return this;
        }

        /// <summary>
        /// Adds a query parameter with boolean value.
        /// </summary>
        public StreamingApiRequestBuilder Query(string name, bool value)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _queryParams.Add(new KeyValuePair<string, string>(name, value ? "true" : "false"));
            }
            return this;
        }

        /// <summary>
        /// Adds multiple query parameters from a dictionary.
        /// </summary>
        public StreamingApiRequestBuilder QueryParams(Dictionary<string, string> parameters)
        {
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    if (!string.IsNullOrEmpty(param.Key))
                    {
                        _queryParams.Add(new KeyValuePair<string, string>(param.Key, param.Value ?? string.Empty));
                    }
                }
            }
            return this;
        }

        /// <summary>
        /// Sets JSON body content for POST/PUT requests.
        /// </summary>
        public StreamingApiRequestBuilder JsonBody(object content)
        {
            _bodyContent = content;
            _headers["Content-Type"] = "application/json";
            return this;
        }

        /// <summary>
        /// Sets form URL encoded body content.
        /// </summary>
        public StreamingApiRequestBuilder FormBody(Dictionary<string, string> formData)
        {
            _bodyContent = formData;
            _headers["Content-Type"] = "application/x-www-form-urlencoded";
            return this;
        }

        /// <summary>
        /// Sets a custom timeout for this request.
        /// </summary>
        public StreamingApiRequestBuilder Timeout(TimeSpan timeout)
        {
            _timeout = timeout;
            return this;
        }

        /// <summary>
        /// Sets common headers for music streaming APIs.
        /// </summary>
        /// <summary>
        /// Applies a resilience policy profile to the request.
        /// </summary>
        public StreamingApiRequestBuilder WithPolicy(ResiliencePolicy policy)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            return this;
        }

        internal ResiliencePolicy? Policy => _policy;

        /// <summary>
        /// Stamps a non-PII auth scope token onto the request for cache/dedup variance when policy opts-in.
        /// The raw scope is hashed via SHA-256 and truncated to 16 hex chars.
        /// </summary>
        public StreamingApiRequestBuilder WithAuthScope(string scopeRaw)
        {
            if (!string.IsNullOrWhiteSpace(scopeRaw))
            {
                var hash = HashingUtility.ComputeSHA256(scopeRaw);
                _authScopeToken = hash.Length > 16 ? hash.Substring(0, 16) : hash;
            }
            return this;
        }

        public StreamingApiRequestBuilder WithStreamingDefaults(string userAgent = null)
        {
            StreamingHeaderDefaults.ApplyTo(_headers, userAgent);
            return this;
        }

        /// <summary>
        /// Adds cache control headers to prevent caching.
        /// </summary>
        public StreamingApiRequestBuilder NoCache()
        {
            _headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            _headers["Pragma"] = "no-cache";
            _headers["Expires"] = "0";
            return this;
        }

        /// <summary>
        /// Builds the final HttpRequestMessage.
        /// </summary>
        public HttpRequestMessage Build()
        {
            var url = BuildUrl();
            var request = new HttpRequestMessage(_method, url);

            // Add headers
            foreach (var header in _headers)
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue; // Will be set with content

                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Add body content
            if (_bodyContent != null && (_method == HttpMethod.Post || _method == HttpMethod.Put))
            {
                var contentType = _headers.GetValueOrDefault("Content-Type", "application/json");

                if (contentType.Contains("application/json"))
                {
                    var json = JsonSerializer.Serialize(_bodyContent);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                else if (contentType.Contains("application/x-www-form-urlencoded") && _bodyContent is Dictionary<string, string> formData)
                {
                    request.Content = new FormUrlEncodedContent(formData);
                }
            }

            // Attach standardized request metadata via HttpRequestMessage.Options
            try
            {
                var endpointForOptions = string.IsNullOrWhiteSpace(_endpoint) ? string.Empty : "/" + _endpoint;
                request.Options.Set(PluginHttpOptions.EndpointKey, endpointForOptions);

                if (_policy != null)
                {
                    request.Options.Set(PluginHttpOptions.ProfileKey, _policy.Name);
                }

                var canonicalQuery = BuildCanonicalQueryString();
                if (!string.IsNullOrEmpty(canonicalQuery))
                {
                    request.Options.Set(PluginHttpOptions.ParametersKey, canonicalQuery);
                }

                if (!string.IsNullOrWhiteSpace(_authScopeToken))
                {
                    request.Options.Set(PluginHttpOptions.AuthScopeKey, _authScopeToken);
                }
            }
            catch
            {
                // Options are best-effort; avoid throwing from builder
            }

            return request;
        }

        /// <summary>
        /// Builds the request and returns information suitable for logging.
        /// Sensitive data (auth headers, tokens) are masked.
        /// </summary>
        public StreamingApiRequestInfo BuildForLogging()
        {
            var url = BuildUrlForLogging();
            var maskedHeaders = HttpClientExtensions.MaskSensitiveParams(_headers);
            var maskedQueryParams = HttpClientExtensions.MaskSensitiveParams(
                new Dictionary<string, string>(_queryParams
                    .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase)));

            return new StreamingApiRequestInfo
            {
                Method = _method.Method,
                Url = url,
                Headers = maskedHeaders,
                QueryParameters = maskedQueryParams,
                HasBody = _bodyContent != null,
                Timeout = _timeout,
                PolicyName = _policy?.Name
            };
        }

        private string BuildUrl()
        {
            var url = string.IsNullOrEmpty(_endpoint) ? _baseUrl : $"{_baseUrl}/{_endpoint}";

            if (_queryParams.Any())
            {
                url = HttpClientExtensions.BuildUrlWithParams(url, _queryParams);
            }

            return url;
        }

        private string BuildUrlForLogging()
        {
            // Do not include query parameter values in log URLs. Query values are a
            // common credential leak vector. Query values (with masking) are logged
            // separately via StreamingApiRequestInfo.QueryParameters.
            var url = string.IsNullOrEmpty(_endpoint) ? _baseUrl : $"{_baseUrl}/{_endpoint}";
            return HttpClientExtensions.BuildUrlWithQueryNames(url, _queryParams);
        }

        private string BuildCanonicalQueryString()
        {
            return Lidarr.Plugin.Common.Utilities.QueryCanonicalizer.Canonicalize(_queryParams);
        }
    }

    /// <summary>
    /// Information about a streaming API request suitable for logging.
    /// </summary>
    public class StreamingApiRequestInfo
    {
        public string Method { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> QueryParameters { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool HasBody { get; set; }
        public string? PolicyName { get; set; }
        public TimeSpan? Timeout { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Method} {Url}");

            if (Headers.Any())
            {
                sb.AppendLine("Headers:");
                foreach (var header in Headers)
                {
                    sb.AppendLine($"  {header.Key}: {header.Value}");
                }
            }

            if (QueryParameters.Any())
            {
                sb.AppendLine("Query Parameters:");
                foreach (var param in QueryParameters)
                {
                    sb.AppendLine($"  {param.Key}: {param.Value}");
                }
            }

            if (HasBody)
            {
                sb.AppendLine("Body: [PRESENT]");
            }

            if (!string.IsNullOrEmpty(PolicyName))
            {
                sb.AppendLine($"Policy: {PolicyName}");
            }

            if (Timeout.HasValue)
            {
                sb.AppendLine($"Timeout: {Timeout.Value.TotalSeconds}s");
            }

            return sb.ToString();
        }
    }
}
