using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Performance;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Interface for enhanced streaming API client with integrated features
    /// </summary>
    public interface IEnhancedStreamingApiClient : IDisposable
    {
        /// <summary>
        /// Makes a GET request with full feature integration
        /// </summary>
        Task<T> GetAsync<T>(string endpoint, Dictionary<string, string> parameters = null, 
            CachePolicy cachePolicy = null, CancellationToken cancellationToken = default);
            
        /// <summary>
        /// Makes a POST request with full feature integration
        /// </summary>
        Task<T> PostAsync<T>(string endpoint, object body = null, Dictionary<string, string> parameters = null,
            CachePolicy cachePolicy = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets authentication token for all subsequent requests
        /// </summary>
        void SetAuthenticationToken(string token, AuthenticationType type = AuthenticationType.Bearer);

        /// <summary>
        /// Gets current rate limiting statistics
        /// </summary>
        ServiceRateLimitStats GetRateLimitStats();

        /// <summary>
        /// Gets cache hit statistics
        /// </summary>
        CacheStats GetCacheStats();
    }

    /// <summary>
    /// Enhanced streaming API client with integrated rate limiting, caching, authentication, and error handling
    /// Consolidates patterns from all successful streaming plugins
    /// </summary>
    /// <remarks>
    /// Integrated Features:
    /// - Universal adaptive rate limiting with per-endpoint tracking
    /// - Response caching with configurable policies
    /// - Automatic authentication header injection
    /// - Retry logic with exponential backoff
    /// - Parameter masking for sensitive data logging
    /// - Request/response logging with privacy protection
    /// - Error handling with streaming service specific patterns
    /// 
    /// Based on proven patterns from:
    /// - Qobuzarr: Advanced rate limiting and error handling
    /// - Tidalarr: OAuth integration and secure operations
    /// - TrevTV's: Simple, reliable API patterns
    /// </remarks>
    public class EnhancedStreamingApiClient : IEnhancedStreamingApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly IUniversalAdaptiveRateLimiter _rateLimiter;
        private readonly IStreamingResponseCache _cache;
        private readonly string _serviceName;
        private readonly string _baseUrl;
        private readonly JsonSerializerSettings _jsonSettings;
        
        private string _authToken = string.Empty;
        private AuthenticationType _authType = AuthenticationType.Bearer;
        private bool _disposed;

        public EnhancedStreamingApiClient(
            HttpClient httpClient,
            string serviceName,
            string baseUrl,
            IUniversalAdaptiveRateLimiter rateLimiter = null,
            IStreamingResponseCache cache = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
            _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _rateLimiter = rateLimiter ?? new UniversalAdaptiveRateLimiter();
            _cache = cache ?? new StreamingResponseCache();

            _jsonSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            };
        }

        public async Task<T> GetAsync<T>(string endpoint, Dictionary<string, string> parameters = null, 
            CachePolicy cachePolicy = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteRequestAsync<T>(HttpMethod.Get, endpoint, null, parameters, cachePolicy, cancellationToken);
        }

        public async Task<T> PostAsync<T>(string endpoint, object body = null, Dictionary<string, string> parameters = null,
            CachePolicy cachePolicy = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteRequestAsync<T>(HttpMethod.Post, endpoint, body, parameters, cachePolicy, cancellationToken);
        }

        public void SetAuthenticationToken(string token, AuthenticationType type = AuthenticationType.Bearer)
        {
            _authToken = token ?? string.Empty;
            _authType = type;
        }

        public ServiceRateLimitStats GetRateLimitStats()
        {
            return _rateLimiter.GetServiceStats(_serviceName);
        }

        public CacheStats GetCacheStats()
        {
            return _cache.GetStats();
        }

        private async Task<T> ExecuteRequestAsync<T>(
            HttpMethod method,
            string endpoint,
            object body = null,
            Dictionary<string, string> parameters = null,
            CachePolicy cachePolicy = null,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EnhancedStreamingApiClient));

            var cacheKey = BuildCacheKey(method, endpoint, parameters, body);

            // Check cache first for GET requests
            if (method == HttpMethod.Get && cachePolicy != null && _cache.TryGet<T>(cacheKey, out var cachedResult))
            {
                return cachedResult;
            }

            // Wait for rate limiting
            await _rateLimiter.WaitIfNeededAsync(_serviceName, endpoint, cancellationToken);

            // Build request
            var request = BuildRequest(method, endpoint, body, parameters);

            HttpResponseMessage response = null;
            try
            {
                // Execute request with retry logic
                response = await _httpClient.ExecuteWithRetryAsync(request, cancellationToken);

                // Record response for rate limiting
                _rateLimiter.RecordResponse(_serviceName, endpoint, response);

                // Handle response
                var content = await response.Content.ReadAsStringAsync();
                
                // Deserialize result
                T result;
                if (typeof(T) == typeof(string))
                {
                    result = (T)(object)content;
                }
                else
                {
                    result = JsonConvert.DeserializeObject<T>(content, _jsonSettings);
                }

                // Cache successful results
                if (method == HttpMethod.Get && cachePolicy != null && response.IsSuccessStatusCode)
                {
                    _cache.Set(cacheKey, result, cachePolicy);
                }

                return result;
            }
            catch (Exception ex)
            {
                // Record failure for rate limiting
                if (response != null)
                {
                    _rateLimiter.RecordResponse(_serviceName, endpoint, response);
                }

                throw new StreamingApiException($"API request to {endpoint} failed", ex)
                {
                    ServiceName = _serviceName,
                    Endpoint = endpoint,
                    HttpStatusCode = response?.StatusCode
                };
            }
            finally
            {
                response?.Dispose();
                request?.Dispose();
            }
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string endpoint, object body, Dictionary<string, string> parameters)
        {
            var requestBuilder = new StreamingApiRequestBuilder(_baseUrl)
                .Endpoint(endpoint)
                .Method(method);

            // Add query parameters
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    requestBuilder.Query(param.Key, param.Value);
                }
            }

            // Add authentication
            if (!string.IsNullOrEmpty(_authToken))
            {
                switch (_authType)
                {
                    case AuthenticationType.Bearer:
                        requestBuilder.BearerToken(_authToken);
                        break;
                    case AuthenticationType.ApiKey:
                        requestBuilder.Header("X-API-Key", _authToken);
                        break;
                    case AuthenticationType.Custom:
                        requestBuilder.Header("Authorization", _authToken);
                        break;
                }
            }

            // Add body for POST requests
            if (body != null && method != HttpMethod.Get)
            {
                requestBuilder.JsonContent(body);
            }

            // Apply common streaming defaults
            requestBuilder.WithStreamingDefaults();

            return requestBuilder.Build();
        }

        private string BuildCacheKey(HttpMethod method, string endpoint, Dictionary<string, string> parameters, object body)
        {
            var key = $"{_serviceName}:{method}:{endpoint}";
            
            if (parameters != null && parameters.Count > 0)
            {
                var sortedParams = string.Join("&", parameters.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
                key += $"?{sortedParams}";
            }

            if (body != null)
            {
                var bodyJson = JsonConvert.SerializeObject(body, _jsonSettings);
                key += $":{bodyJson.GetHashCode()}";
            }

            return key;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _rateLimiter?.Dispose();
            _cache?.Dispose();
        }
    }

    /// <summary>
    /// Cache policy configuration for API responses
    /// </summary>
    public class CachePolicy
    {
        /// <summary>
        /// How long to cache the response
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Whether to serve stale cache entries if refresh fails
        /// </summary>
        public bool AllowStale { get; set; } = false;

        /// <summary>
        /// Cache tags for bulk invalidation
        /// </summary>
        public List<string> Tags { get; set; } = new();

        public static CachePolicy Short => new() { Duration = TimeSpan.FromMinutes(5) };
        public static CachePolicy Medium => new() { Duration = TimeSpan.FromMinutes(30) };
        public static CachePolicy Long => new() { Duration = TimeSpan.FromHours(2) };
        public static CachePolicy VeryLong => new() { Duration = TimeSpan.FromDays(1) };
    }

    /// <summary>
    /// Authentication types supported by the API client
    /// </summary>
    public enum AuthenticationType
    {
        Bearer,
        ApiKey,
        Custom
    }

    /// <summary>
    /// Statistics for cache performance monitoring
    /// </summary>
    public class CacheStats
    {
        public long Hits { get; set; }
        public long Misses { get; set; }
        public long Evictions { get; set; }
        public long CurrentEntries { get; set; }
        public double HitRate => (Hits + Misses) > 0 ? (double)Hits / (Hits + Misses) : 0;
    }

    /// <summary>
    /// Exception thrown by streaming API operations
    /// </summary>
    public class StreamingApiException : Exception
    {
        public string ServiceName { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public System.Net.HttpStatusCode? HttpStatusCode { get; set; }

        public StreamingApiException(string message) : base(message) { }
        public StreamingApiException(string message, Exception innerException) : base(message, innerException) { }
    }
}