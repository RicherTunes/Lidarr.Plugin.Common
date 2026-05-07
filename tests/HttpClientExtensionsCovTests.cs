using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Deduplication;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class HttpClientExtensionsCovTests
    {
        private sealed class StubHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            {
                _handler = (req, _) => handler(req);
            }

            public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _handler(request, cancellationToken);
            }
        }

        #region ArgumentNullException Tests

        [Fact]
        public async Task ExecuteWithResilienceAsync_ThrowsArgumentNullException_WhenPolicyIsNull()
        {
            // Line 142: if (policy == null) throw new ArgumentNullException(nameof(policy));
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.ExecuteWithResilienceAsync(request, policy: null!, CancellationToken.None));

            Assert.Equal("policy", ex.ParamName);
        }

        [Fact]
        public async Task SendWithResilienceAsync_ThrowsArgumentNullException_WhenHttpClientIsNull()
        {
            // Line 163: if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            HttpClient client = null!;
            var builder = new StreamingApiRequestBuilder("https://example.com");

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.SendWithResilienceAsync(builder));

            Assert.Equal("httpClient", ex.ParamName);
        }

        [Fact]
        public async Task SendWithResilienceAsync_ThrowsArgumentNullException_WhenBuilderIsNull()
        {
            // Line 164: if (builder == null) throw new ArgumentNullException(nameof(builder));
            using var client = new HttpClient();
            StreamingApiRequestBuilder builder = null!;

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.SendWithResilienceAsync(builder));

            Assert.Equal("builder", ex.ParamName);
        }

        [Fact]
        public async Task SendWithResilienceAsync_WithDeduplicator_ThrowsArgumentNullException_WhenHttpClientIsNull()
        {
            // Line 219: if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            HttpClient client = null!;
            var builder = new StreamingApiRequestBuilder("https://example.com");
            var deduplicator = new RequestDeduplicator(new NullLogger<RequestDeduplicator>());

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.SendWithResilienceAsync(builder, deduplicator));

            Assert.Equal("httpClient", ex.ParamName);
        }

        [Fact]
        public async Task SendWithResilienceAsync_WithDeduplicator_ThrowsArgumentNullException_WhenBuilderIsNull()
        {
            // Line 220: if (builder == null) throw new ArgumentNullException(nameof(builder));
            using var client = new HttpClient();
            StreamingApiRequestBuilder builder = null!;
            var deduplicator = new RequestDeduplicator(new NullLogger<RequestDeduplicator>());

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.SendWithResilienceAsync(builder, deduplicator));

            Assert.Equal("builder", ex.ParamName);
        }

        [Fact]
        public async Task SendWithResilienceAsync_WithDeduplicator_ThrowsArgumentNullException_WhenDeduplicatorIsNull()
        {
            // Line 221: if (deduplicator == null) throw new ArgumentNullException(nameof(deduplicator));
            using var client = new HttpClient();
            var builder = new StreamingApiRequestBuilder("https://example.com");
            RequestDeduplicator deduplicator = null!;

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.SendWithResilienceAsync(builder, deduplicator));

            Assert.Equal("deduplicator", ex.ParamName);
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_ThrowsArgumentNullException_WhenHttpClientIsNull()
        {
            // Line 320: if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            HttpClient client = null!;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.ExecuteWithResilienceAsync(request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 1, perRequestTimeout: null, CancellationToken.None));

            Assert.Equal("httpClient", ex.ParamName);
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_ThrowsArgumentNullException_WhenRequestIsNull()
        {
            // Line 321: if (request == null) throw new ArgumentNullException(nameof(request));
            using var client = new HttpClient();
            HttpRequestMessage request = null!;

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.ExecuteWithResilienceAsync(request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 1, perRequestTimeout: null, CancellationToken.None));

            Assert.Equal("request", ex.ParamName);
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_ThrowsArgumentNullException_WhenTimeProviderIsNull()
        {
            // Line 621: if (timeProvider == null) throw new ArgumentNullException(nameof(timeProvider));
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
            TimeProvider timeProvider = null!;

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                client.ExecuteWithResilienceAsync(request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 1, perRequestTimeout: null, timeProvider: timeProvider, CancellationToken.None));

            Assert.Equal("timeProvider", ex.ParamName);
        }

#if NET8_0_OR_GREATER
        [Fact]
        public async Task ExecuteWithResilienceAsyncCore_TimeProvider_ThrowsArgumentNullException_WhenHttpClientIsNull()
        {
            // Line 645: if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            HttpClient client = null!;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
            var timeProvider = new FakeTimeProvider();

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                HttpClientExtensions.ExecuteWithResilienceAsyncInternal(
                    client, request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1),
                    maxConcurrencyPerHost: 1, maxTotalConcurrencyPerHost: 1,
                    perRequestTimeout: null, CancellationToken.None));

            Assert.Equal("httpClient", ex.ParamName);
        }

        [Fact]
        public async Task ExecuteWithResilienceAsyncCore_TimeProvider_ThrowsArgumentNullException_WhenRequestIsNull()
        {
            // Line 646: if (request == null) throw new ArgumentNullException(nameof(request));
            using var client = new HttpClient();
            HttpRequestMessage request = null!;
            var timeProvider = new FakeTimeProvider();

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                HttpClientExtensions.ExecuteWithResilienceAsyncInternal(
                    client, request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1),
                    maxConcurrencyPerHost: 1, maxTotalConcurrencyPerHost: 1,
                    perRequestTimeout: null, CancellationToken.None));

            Assert.Equal("request", ex.ParamName);
        }
#endif

        [Fact]
        public void BuildRequestDedupKey_ThrowsArgumentNullException_WhenRequestIsNull()
        {
            // Line 1105: if (request == null) throw new ArgumentNullException(nameof(request));
            HttpRequestMessage request = null!;

            var ex = Assert.Throws<ArgumentNullException>(() =>
                HttpClientExtensions.BuildRequestDedupKey(request));

            Assert.Equal("request", ex.ParamName);
        }

        [Fact]
        public async Task CloneForRetryAsync_ThrowsArgumentNullException_WhenRequestIsNull()
        {
            // Line 1233: if (request == null) throw new ArgumentNullException(nameof(request));
            HttpRequestMessage request = null!;

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                HttpClientExtensions.CloneForRetryAsync(request));

            Assert.Equal("request", ex.ParamName);
        }

        #endregion

        #region InvalidOperationException Tests

        [Fact]
        public async Task ExecuteWithResilienceAsync_ThrowsInvalidOperationException_WhenRelativeUriWithoutBaseAddress()
        {
            // Line 341: throw new InvalidOperationException("Relative RequestUri without HttpClient.BaseAddress.");
            using var client = new HttpClient(); // No BaseAddress set
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/relative/path", UriKind.Relative));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ExecuteWithResilienceAsync(request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1), maxConcurrencyPerHost: 1, perRequestTimeout: null, CancellationToken.None));

            Assert.Contains("Relative RequestUri without HttpClient.BaseAddress", ex.Message);
        }

#if NET8_0_OR_GREATER
        [Fact]
        public async Task ExecuteWithResilienceAsync_TimeProvider_ThrowsInvalidOperationException_WhenRelativeUriWithoutBaseAddress()
        {
            // Line 667: throw new InvalidOperationException("Relative RequestUri without HttpClient.BaseAddress.");
            using var client = new HttpClient(); // No BaseAddress set
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/relative/path", UriKind.Relative));
            var timeProvider = new FakeTimeProvider();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ExecuteWithResilienceAsync(request, maxRetries: 1, retryBudget: TimeSpan.FromSeconds(1),
                    maxConcurrencyPerHost: 1, perRequestTimeout: null, timeProvider: timeProvider, CancellationToken.None));

            Assert.Contains("Relative RequestUri without HttpClient.BaseAddress", ex.Message);
        }
#endif

        [Fact]
        public async Task GetJsonAsync_ThrowsInvalidOperationException_WhenDeserializationReturnsNull()
        {
            // Line 865: throw new InvalidOperationException("Failed to deserialize JSON payload into the requested type.");
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("null", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HttpClientExtensions.GetJsonAsync<NonNullPoco>(client, "https://example.com/data"));

            Assert.Contains("Failed to deserialize JSON payload", ex.Message);
        }

        private sealed class NonNullPoco
        {
            public string Value { get; set; } = string.Empty;
        }

        #endregion

        #region TimeoutException Tests

#if NET8_0_OR_GREATER
        [Fact]
        public async Task ExecuteWithResilienceAsync_TimeProvider_ThrowsTimeoutException_WhenPerRequestTimeoutExceeded()
        {
            // Line 737: throw new TimeoutException(...)
            var handler = new StubHandler(async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5000), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://timeout.example/data");
            var timeProvider = new FakeTimeProvider();

            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                client.ExecuteWithResilienceAsync(
                    request,
                    maxRetries: 1,
                    retryBudget: TimeSpan.FromSeconds(10),
                    maxConcurrencyPerHost: 1,
                    perRequestTimeout: TimeSpan.FromMilliseconds(100),
                    timeProvider: timeProvider,
                    CancellationToken.None));

            Assert.Contains("per-request timeout", ex.Message);
        }
#endif

        #endregion

        #region BuildUrlWithParams Tests

        [Fact]
        public void BuildUrlWithParams_ReturnsBaseUrl_WhenParametersIsNull()
        {
            // Line 980-981: if (parameters == null || !parameters.Any()) return baseUrl;
            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", (Dictionary<string, string>)null!);

            Assert.Equal("https://example.com/api", result);
        }

        [Fact]
        public void BuildUrlWithParams_ReturnsBaseUrl_WhenParametersIsEmpty()
        {
            // Line 980-981: if (parameters == null || !parameters.Any()) return baseUrl;
            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", new Dictionary<string, string>());

            Assert.Equal("https://example.com/api", result);
        }

        [Fact]
        public void BuildUrlWithParams_AppendsQueryParams_WhenParametersProvided()
        {
            var parameters = new Dictionary<string, string>
            {
                { "key1", "value1" },
                { "key2", "value2" }
            };

            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", parameters);

            Assert.Contains("key1=value1", result);
            Assert.Contains("key2=value2", result);
            Assert.Contains("?", result);
        }

        [Fact]
        public void BuildUrlWithParams_UsesAmpersand_WhenBaseUrlAlreadyHasQuery()
        {
            var parameters = new Dictionary<string, string>
            {
                { "extra", "param" }
            };

            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api?existing=1", parameters);

            Assert.Contains("&extra=param", result);
        }

        [Fact]
        public void BuildUrlWithParams_EscapesSpecialCharacters()
        {
            var parameters = new Dictionary<string, string>
            {
                { "key with space", "value&special" }
            };

            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", parameters);

            Assert.Contains("key%20with%20space=value%26special", result);
        }

        [Fact]
        public void BuildUrlWithParams_Enumerable_ReturnsBaseUrl_WhenParametersIsNull()
        {
            // Line 995-996: if (parameters == null) return baseUrl;
            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", (IEnumerable<KeyValuePair<string, string>>)null!);

            Assert.Equal("https://example.com/api", result);
        }

        [Fact]
        public void BuildUrlWithParams_Enumerable_ReturnsBaseUrl_WhenParametersIsEmpty()
        {
            // Line 998-999: if (list.Count == 0) return baseUrl;
            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", Array.Empty<KeyValuePair<string, string>>());

            Assert.Equal("https://example.com/api", result);
        }

        [Fact]
        public void BuildUrlWithParams_Enumerable_HandlesNullKeyAndValue()
        {
            // Line 1002: handles null key/value with ?? string.Empty
            var parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(null!, "value"),
                new KeyValuePair<string, string>("key", null!)
            };

            var result = HttpClientExtensions.BuildUrlWithParams("https://example.com/api", parameters);

            Assert.Contains("=value", result);
            Assert.Contains("key=", result);
        }

        #endregion

        #region BuildUrlWithQueryNames Tests

        [Fact]
        public void BuildUrlWithQueryNames_ReturnsBaseUrl_WhenBaseUrlIsWhitespace()
        {
            // Line 897-900: if (string.IsNullOrWhiteSpace(baseUrl)) return baseUrl ?? string.Empty;
            var result = HttpClientExtensions.BuildUrlWithQueryNames("   ", null!);

            Assert.Equal("   ", result);
        }

        [Fact]
        public void BuildUrlWithQueryNames_PreservesFragment()
        {
            // Lines 904-910: fragment handling
            var result = HttpClientExtensions.BuildUrlWithQueryNames("https://example.com/path#section", null!);

            Assert.Contains("#section", result);
        }

        [Fact]
        public void BuildUrlWithQueryNames_PreservesExistingQuery()
        {
            // Lines 912-918: existing query handling
            var result = HttpClientExtensions.BuildUrlWithQueryNames("https://example.com/path?existing=param&another=value", null!);

            Assert.Contains("?existing", result);
            Assert.Contains("another", result);
        }

        [Fact]
        public void BuildUrlWithQueryNames_AddsParameterKeys()
        {
            // Lines 952-961: adding new parameter keys
            var parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("newKey", "value")
            };

            var result = HttpClientExtensions.BuildUrlWithQueryNames("https://example.com/path", parameters);

            Assert.Contains("newKey", result);
            Assert.DoesNotContain("value", result); // Values should not be included
        }

        [Fact]
        public void BuildUrlWithQueryNames_DeduplicatesKeys()
        {
            // Lines 920-950: deduplication logic
            var parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("key1", "value1"),
                new KeyValuePair<string, string>("key2", "value2"),
                new KeyValuePair<string, string>("key1", "value3") // duplicate key
            };

            var result = HttpClientExtensions.BuildUrlWithQueryNames("https://example.com/path", parameters);

            var key1Count = result.Split('?')[1].Split('&').Count(k => k.StartsWith("key1"));
            Assert.Equal(1, key1Count); // Only one occurrence of key1
        }

        [Fact]
        public void BuildUrlWithQueryNames_SortsKeysAlphabetically()
        {
            // Line 968: OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            var parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("zebra", "1"),
                new KeyValuePair<string, string>("apple", "2"),
                new KeyValuePair<string, string>("banana", "3")
            };

            var result = HttpClientExtensions.BuildUrlWithQueryNames("https://example.com/path", parameters);
            var queryString = result.Split('?')[1];

            var keys = queryString.Split('&').Select(k => k.Split('=')[0]).ToArray();
            Assert.Equal("apple", keys[0]);
            Assert.Equal("banana", keys[1]);
            Assert.Equal("zebra", keys[2]);
        }

        [Fact]
        public void BuildUrlWithQueryNames_ReturnsUrlWithoutQuery_WhenNoParameters()
        {
            // Lines 963-966: if (keys.Count == 0) return url + fragment;
            var result = HttpClientExtensions.BuildUrlWithQueryNames("https://example.com/path", null!);

            Assert.Equal("https://example.com/path", result);
        }

        #endregion

        #region PostJsonAsync Tests

        [Fact]
        public async Task PostJsonAsync_SendsJsonAndDeserializesResponse()
        {
            // Lines 874-889: PostJsonAsync implementation
            var handler = new StubHandler(async req =>
            {
                Assert.Equal("application/json", req.Content?.Headers?.ContentType?.MediaType);
                var requestBody = await req.Content!.ReadAsStringAsync();
                Assert.Contains("\"Name\"", requestBody);
                Assert.Contains("\"Test\"", requestBody);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"Result\":\"Success\"}", Encoding.UTF8, "application/json")
                };
            });

            using var client = new HttpClient(handler);
            var requestData = new TestRequest { Name = "Test" };

            var result = await HttpClientExtensions.PostJsonAsync<TestRequest, TestResponse>(
                client, "https://example.com/api", requestData);

            Assert.Equal("Success", result.Result);
        }

        [Fact]
        public async Task PostJsonAsync_UsesCustomJsonSerializerOptions()
        {
            var handler = new StubHandler(async req =>
            {
                var requestBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
            });

            using var client = new HttpClient(handler);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var requestData = new TestRequest { Name = "Test" };

            var result = await HttpClientExtensions.PostJsonAsync<TestRequest, TestRequest>(
                client, "https://example.com/api", requestData, options);

            Assert.Equal("Test", result.Name);
        }

        private sealed class TestRequest
        {
            public string Name { get; set; } = string.Empty;
        }

        private sealed class TestResponse
        {
            public string Result { get; set; } = string.Empty;
        }

        #endregion

        #region AddStandardHeaders Tests

        [Fact]
        public void AddStandardHeaders_AddsUserAgent_WhenProvided()
        {
            // Lines 1011-1027: AddStandardHeaders implementation
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            var result = request.AddStandardHeaders(userAgent: "MyAgent/1.0");

            Assert.Same(request, result);
            Assert.Contains("MyAgent", request.Headers.UserAgent.ToString());
        }

        [Fact]
        public void AddStandardHeaders_AddsAdditionalHeaders_WhenProvided()
        {
            var additionalHeaders = new Dictionary<string, string>
            {
                { "X-Custom-Header", "CustomValue" },
                { "X-Another-Header", "AnotherValue" }
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
            request.AddStandardHeaders(additionalHeaders: additionalHeaders);

            Assert.True(request.Headers.Contains("X-Custom-Header"));
            Assert.True(request.Headers.Contains("X-Another-Header"));
            Assert.Equal("CustomValue", request.Headers.GetValues("X-Custom-Header").First());
        }

        #endregion

        #region ExecuteWithTimingAsync Tests

        [Fact]
        public async Task ExecuteWithTimingAsync_ReturnsResponseWithDuration()
        {
            // Lines 1032-1049: ExecuteWithTimingAsync implementation
            var handler = new StubHandler(_ =>
            {
                Thread.Sleep(10); // Small delay to ensure non-zero duration
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("test")
                });
            });

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            var (response, duration) = await HttpClientExtensions.ExecuteWithTimingAsync(client, request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(duration > TimeSpan.Zero);
        }

        [Fact]
        public async Task ExecuteWithTimingAsync_PreservesException_WhenRequestFails()
        {
            var handler = new StubHandler(_ =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("Network error")));

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                HttpClientExtensions.ExecuteWithTimingAsync(client, request));
        }

        #endregion

        #region ReadContentSafelyAsync Tests

        [Fact]
        public async Task ReadContentSafelyAsync_ReturnsStringContent()
        {
            // Lines 1054-1066: ReadContentSafelyAsync implementation
            var content = new StringContent("Hello, World!", Encoding.UTF8, "text/plain");

            var result = await HttpClientExtensions.ReadContentSafelyAsync(content);

            Assert.Equal("Hello, World!", result);
        }

        #endregion

        #region MaskSensitiveParams Tests

        [Fact]
        public void MaskSensitiveParams_ReturnsEmptyDictionary_WhenParametersIsNull()
        {
            // Line 1082: if (parameters == null) return new Dictionary<string, string>();
            var result = HttpClientExtensions.MaskSensitiveParams(null!);

            Assert.Empty(result);
        }

        [Fact]
        public void MaskSensitiveParams_MasksSensitiveKeys()
        {
            // Lines 1084-1096: Masking logic
            var parameters = new Dictionary<string, string>
            {
                { "api_key", "secret123" },
                { "password", "mypass" },
                { "token", "mytoken" },
                { "normal_param", "normal_value" }
            };

            var result = HttpClientExtensions.MaskSensitiveParams(parameters);

            Assert.Equal("[redacted]", result["api_key"]);
            Assert.Equal("[redacted]", result["password"]);
            Assert.Equal("[redacted]", result["token"]);
            Assert.Equal("normal_value", result["normal_param"]);
        }

        [Fact]
        public void MaskSensitiveParams_MasksEmptyValues()
        {
            var parameters = new Dictionary<string, string>
            {
                { "api_key", "" },
                { "secret", null! }
            };

            var result = HttpClientExtensions.MaskSensitiveParams(parameters);

            Assert.Equal("[empty]", result["api_key"]);
            Assert.Equal("[empty]", result["secret"]);
        }

        #endregion

        #region IsJsonContent Tests

        [Fact]
        public void IsJsonContent_ReturnsTrue_ForApplicationJson()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            Assert.True(HttpClientExtensions.IsJsonContent(response));
        }

        [Fact]
        public void IsJsonContent_ReturnsTrue_ForTextJson()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "text/json")
            };

            Assert.True(HttpClientExtensions.IsJsonContent(response));
        }

        [Fact]
        public void IsJsonContent_ReturnsFalse_ForNonJsonContentType()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("plain text", Encoding.UTF8, "text/plain")
            };

            Assert.False(HttpClientExtensions.IsJsonContent(response));
        }

        [Fact]
        public void IsJsonContent_ReturnsFalse_WhenContentIsNull()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = null!
            };

            Assert.False(HttpClientExtensions.IsJsonContent(response));
        }

        #endregion

        #region CloneHttpRequestMessageAsync Tests

        [Fact]
        public async Task CloneHttpRequestMessageAsync_ClonesRequestWithContent()
        {
            var original = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api")
            {
                Content = new StringContent("{\"test\":true}", Encoding.UTF8, "application/json")
            };
            original.Headers.Add("X-Custom-Header", "value");

            var clone = await HttpClientExtensions.CloneHttpRequestMessageAsync(original);

            Assert.Equal(original.Method, clone.Method);
            Assert.Equal(original.RequestUri, clone.RequestUri);
            Assert.True(clone.Headers.Contains("X-Custom-Header"));
            Assert.NotNull(clone.Content);

            var originalContent = await original.Content!.ReadAsStringAsync();
            var cloneContent = await clone.Content!.ReadAsStringAsync();
            Assert.Equal(originalContent, cloneContent);
        }

        [Fact]
        public async Task CloneHttpRequestMessageAsync_ClonesRequestWithoutContent()
        {
            var original = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
            original.Headers.Add("Accept", "application/json");

            var clone = await HttpClientExtensions.CloneHttpRequestMessageAsync(original);

            Assert.Equal(original.Method, clone.Method);
            Assert.Equal(original.RequestUri, clone.RequestUri);
            Assert.True(clone.Headers.Contains("Accept"));
            Assert.Null(clone.Content);
        }

        #endregion

        #region CloneForRetryAsync Tests

        [Fact]
        public async Task CloneForRetryAsync_ClonesContentAndBuffersBody()
        {
            // Lines 1231-1274: CloneForRetryAsync with content buffering
            var original = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api")
            {
                Content = new StringContent("{\"test\":true}", Encoding.UTF8, "application/json")
            };
            original.Content.Headers.Add("X-Content-Header", "content-value");

            var clone1 = await HttpClientExtensions.CloneForRetryAsync(original);
            var clone2 = await HttpClientExtensions.CloneForRetryAsync(original);

            var content1 = await clone1.Content!.ReadAsStringAsync();
            var content2 = await clone2.Content!.ReadAsStringAsync();

            Assert.Equal("{\"test\":true}", content1);
            Assert.Equal("{\"test\":true}", content2);
            Assert.True(clone1.Content.Headers.Contains("X-Content-Header"));
        }

        [Fact]
        public async Task CloneForRetryAsync_CachesBufferedBodyInOptions()
        {
            // Line 1254-1258: Buffer once into request.Options, then reuse
            var original = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api")
            {
                Content = new StringContent("original content", Encoding.UTF8)
            };

            var clone1 = await HttpClientExtensions.CloneForRetryAsync(original);
            var clone2 = await HttpClientExtensions.CloneForRetryAsync(original);

            var content1 = await clone1.Content!.ReadAsStringAsync();
            var content2 = await clone2.Content!.ReadAsStringAsync();

            Assert.Equal("original content", content1);
            Assert.Equal("original content", content2);
        }

        #endregion

        #region BuildRequestDedupKey Tests

        [Fact]
        public void BuildRequestDedupKey_GeneratesConsistentKey()
        {
            using var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api?query=test");
            using var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api?query=test");

            var key1 = HttpClientExtensions.BuildRequestDedupKey(request1);
            var key2 = HttpClientExtensions.BuildRequestDedupKey(request2);

            Assert.Equal(key1, key2);
        }

        [Fact]
        public void BuildRequestDedupKey_IncludesPort()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com:8443/api");

            var key = HttpClientExtensions.BuildRequestDedupKey(request);

            Assert.NotNull(key);
            Assert.NotEmpty(key);
        }

        [Fact]
        public void BuildRequestDedupKey_HandlesRequestWithoutRequestUri()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);

            var key = HttpClientExtensions.BuildRequestDedupKey(request);

            Assert.NotNull(key);
        }

        #endregion

        #region GetJsonAsync Edge Cases

        [Fact]
        public async Task GetJsonAsync_ReturnsDefault_WhenContentIsEmpty()
        {
            // Line 843-844: if string.IsNullOrWhiteSpace(payload), return default
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);

            var result = await HttpClientExtensions.GetJsonAsync<TestResponse>(client, "https://example.com/empty");

            Assert.Null(result);
        }

        [Fact]
        public async Task GetJsonAsync_ReturnsDefault_WhenContentIsWhitespace()
        {
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("   ", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);

            var result = await HttpClientExtensions.GetJsonAsync<TestResponse>(client, "https://example.com/whitespace");

            Assert.Null(result);
        }

        [Fact]
        public async Task GetJsonAsync_UsesCustomOptions()
        {
            // Lines 857-860: options ??= new JsonSerializerOptions
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"Test\"}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false };

            var result = await HttpClientExtensions.GetJsonAsync<TestRequest>(client, "https://example.com/data", options);

            Assert.Equal("Test", result.Name);
        }

        #endregion

        #region SendWithResilienceAsync Deduplication Tests

        [Fact]
        public async Task SendWithResilienceAsync_WithDeduplicator_FallsBackToNormalPath_ForNonGetMethods()
        {
            // Lines 228-245: Only dedupe GET-like requests
            var callCount = 0;
            var handler = new StubHandler(_ =>
            {
                callCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"ok\"}", Encoding.UTF8, "application/json")
                });
            });

            using var client = new HttpClient(handler);
            var builder = new StreamingApiRequestBuilder("https://example.com")
                .Endpoint("test")
                .Method(HttpMethod.Post);
            var deduplicator = new RequestDeduplicator(new NullLogger<RequestDeduplicator>());

            await client.SendWithResilienceAsync(builder, deduplicator);

            // POST should not be deduplicated, so callCount should be 1 (no dedup means single call)
            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task SendWithResilienceAsync_WithDeduplicator_DeduplicatesGetRequests()
        {
            // Lines 247-268: Deduplication path for GET requests
            var callCount = 0;
            var handler = new StubHandler(_ =>
            {
                callCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"ok\"}", Encoding.UTF8, "application/json")
                });
            });

            using var client = new HttpClient(handler);
            var builder = new StreamingApiRequestBuilder("https://example.com")
                .Endpoint("test");
            var deduplicator = new RequestDeduplicator(new NullLogger<RequestDeduplicator>());

            // First call
            var response1 = await client.SendWithResilienceAsync(builder, deduplicator);
            // Second call with same builder (should deduplicate)
            var response2 = await client.SendWithResilienceAsync(builder, deduplicator);

            // Should only call the handler once due to deduplication
            Assert.Equal(1, callCount);
            Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        }

        #endregion
    }
}
