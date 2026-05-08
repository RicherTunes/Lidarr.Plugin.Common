using System;
using System.Collections.Generic;
using System.Net.Http;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Coverage tests for StreamingApiRequestBuilder - targets uncovered branches.
    /// </summary>
    [Trait("Category", "Unit")]
    public class StreamingApiRequestBuilderCovTests
    {
        private const string BaseUrl = "https://api.example.test/v1";

        #region Constructor Edge Cases

        [Fact]
        public void Constructor_EmptyString_Accepted()
        {
            // Line 30: baseUrl?.TrimEnd('/') returns empty string, not null
            var builder = new StreamingApiRequestBuilder("");
            var request = builder.Build();
            // Empty base URL results in null RequestUri (HttpRequestMessage normalizes "" to null)
            Assert.Null(request.RequestUri);
        }

        #endregion

        #region Endpoint Edge Cases

        [Fact]
        public void Endpoint_EmptyString_UsesBaseUrlOnly()
        {
            // Line 38: _endpoint = endpoint?.TrimStart('/') => empty string
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("")
                .Build();

            // Empty endpoint results in base URL without trailing slash
            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
        }

        #endregion

        #region WithAuthScope - Lines 223-231

        [Fact]
        public void WithAuthScope_ValidScope_SetsAuthScopeToken()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithAuthScope("user:read email:read")
                .Build();

            // Line 300-303: Auth scope is set via Options
            Assert.True(request.Options.TryGetValue(PluginHttpOptions.AuthScopeKey, out var token));
            Assert.False(string.IsNullOrWhiteSpace(token));
            // Should be 16 hex chars (truncated SHA256)
            Assert.Equal(16, token!.Length);
        }

        [Fact]
        public void WithAuthScope_NullScope_NotSet()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithAuthScope(null!)
                .Build();

            Assert.False(request.Options.TryGetValue(PluginHttpOptions.AuthScopeKey, out _));
        }

        [Fact]
        public void WithAuthScope_EmptyScope_NotSet()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithAuthScope("")
                .Build();

            Assert.False(request.Options.TryGetValue(PluginHttpOptions.AuthScopeKey, out _));
        }

        [Fact]
        public void WithAuthScope_WhitespaceScope_NotSet()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithAuthScope("   ")
                .Build();

            Assert.False(request.Options.TryGetValue(PluginHttpOptions.AuthScopeKey, out _));
        }

        #endregion

        #region WithStreamingDefaults - Lines 233-237

        [Fact]
        public void WithStreamingDefaults_WithoutUserAgent_SetsDefaultHeaders()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithStreamingDefaults()
                .Build();

            Assert.True(request.Headers.Contains("Accept"));
            Assert.Contains("application/json", request.Headers.GetValues("Accept"));
            Assert.True(request.Headers.Contains("Accept-Language"));
        }

        [Fact]
        public void WithStreamingDefaults_WithUserAgent_SetsUserAgentHeader()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithStreamingDefaults("MyApp/1.0")
                .Build();

            Assert.True(request.Headers.Contains("User-Agent"));
            Assert.Contains("MyApp/1.0", request.Headers.GetValues("User-Agent"));
        }

        #endregion

        #region Build() - PUT with Body Content - Lines 268-281

        [Fact]
        public void JsonBody_Put_SetsContentTypeAndContent()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Put()
                .JsonBody(new { id = 123, name = "Updated" })
                .Build();

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        }

        [Fact]
        public void FormBody_Put_SetsFormUrlEncodedContent()
        {
            var formData = new Dictionary<string, string>
            {
                { "action", "update" },
                { "id", "456" }
            };

            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Put()
                .FormBody(formData)
                .Build();

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.NotNull(request.Content);
            Assert.IsType<FormUrlEncodedContent>(request.Content);
        }

        #endregion

        #region Build() - DELETE Method Body Handling - Line 268

        [Fact]
        public void JsonBody_Delete_NoContentAttached()
        {
            // Line 268: Body only attached for POST/PUT
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Delete()
                .JsonBody(new { reason = "test" })
                .Build();

            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Null(request.Content);
        }

        #endregion

        #region Build() - HttpRequestMessage Options - Lines 284-308

        [Fact]
        public void Build_WithEndpoint_SetsEndpointOption()
        {
            // Lines 286-287
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("albums/123")
                .Build();

            Assert.True(request.Options.TryGetValue(PluginHttpOptions.EndpointKey, out var endpoint));
            Assert.Equal("/albums/123", endpoint);
        }

        [Fact]
        public void Build_WithoutEndpoint_SetsEmptyEndpointOption()
        {
            // Lines 286-287: empty endpoint -> empty string
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Build();

            Assert.True(request.Options.TryGetValue(PluginHttpOptions.EndpointKey, out var endpoint));
            Assert.Equal("", endpoint);
        }

        [Fact]
        public void Build_WithPolicy_SetsProfileOption()
        {
            // Lines 289-292
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .WithPolicy(ResiliencePolicy.Search)
                .Build();

            Assert.True(request.Options.TryGetValue(PluginHttpOptions.ProfileKey, out var profile));
            Assert.Equal("search", profile);
        }

        [Fact]
        public void Build_WithoutPolicy_NoProfileOption()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Build();

            Assert.False(request.Options.TryGetValue(PluginHttpOptions.ProfileKey, out _));
        }

        [Fact]
        public void Build_WithQueryParams_SetsParametersOption()
        {
            // Lines 294-298: canonical query string
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Query("q", "test")
                .Query("limit", "10")
                .Build();

            Assert.True(request.Options.TryGetValue(PluginHttpOptions.ParametersKey, out var parameters));
            Assert.Contains("q", parameters!);
            Assert.Contains("limit", parameters!);
        }

        [Fact]
        public void Build_WithoutQueryParams_NoParametersOption()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Build();

            Assert.False(request.Options.TryGetValue(PluginHttpOptions.ParametersKey, out _));
        }

        #endregion

        #region BuildForLogging - Method Variations

        [Fact]
        public void BuildForLogging_PostMethod_ReturnsPost()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .Post()
                .BuildForLogging();

            Assert.Equal("POST", info.Method);
        }

        [Fact]
        public void BuildForLogging_PutMethod_ReturnsPut()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .Put()
                .BuildForLogging();

            Assert.Equal("PUT", info.Method);
        }

        [Fact]
        public void BuildForLogging_DeleteMethod_ReturnsDelete()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .Delete()
                .BuildForLogging();

            Assert.Equal("DELETE", info.Method);
        }

        #endregion

        #region BuildForLogging - With Headers

        [Fact]
        public void BuildForLogging_WithHeaders_IncludesMaskedHeaders()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .BearerToken("secret-token")
                .Header("X-Custom", "value")
                .BuildForLogging();

            Assert.True(info.Headers.Count > 0);
            // Authorization should be masked
            Assert.Equal("[redacted]", info.Headers["Authorization"]);
            Assert.Equal("value", info.Headers["X-Custom"]);
        }

        #endregion

        #region QueryParams - Edge Cases - Lines 161-174

        [Fact]
        public void QueryParams_WithNullValueInEntry_AddsWithEmptyValue()
        {
            // Line 169: null value becomes empty string
            var parameters = new Dictionary<string, string>
            {
                { "key", null! }
            };

            var request = new StreamingApiRequestBuilder(BaseUrl)
                .QueryParams(parameters)
                .Build();

            var uri = request.RequestUri!.ToString();
            Assert.Contains("key=", uri);
        }

        [Fact]
        public void QueryParams_WithEmptyKeyInEntry_SkipsEntry()
        {
            // Line 167: empty key is skipped
            var parameters = new Dictionary<string, string>
            {
                { "", "value" },
                { "valid", "param" }
            };

            var request = new StreamingApiRequestBuilder(BaseUrl)
                .QueryParams(parameters)
                .Build();

            var uri = request.RequestUri!.ToString();
            Assert.DoesNotContain("=value", uri);
            Assert.Contains("valid=param", uri);
        }

        #endregion

        #region StreamingApiRequestInfo ToString - Full Coverage - Lines 378-417

        [Fact]
        public void StreamingApiRequestInfo_ToString_WithHeaders_IncludesHeaders()
        {
            // Lines 383-389
            var info = new StreamingApiRequestInfo
            {
                Method = "GET",
                Url = "https://example.test",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Authorization", "Bearer token" },
                    { "X-Custom", "value" }
                }
            };

            var text = info.ToString();
            Assert.Contains("Headers:", text);
            Assert.Contains("Authorization:", text);
            Assert.Contains("X-Custom:", text);
        }

        [Fact]
        public void StreamingApiRequestInfo_ToString_WithQueryParameters_IncludesParameters()
        {
            // Lines 392-398
            var info = new StreamingApiRequestInfo
            {
                Method = "GET",
                Url = "https://example.test",
                QueryParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "q", "test" },
                    { "limit", "10" }
                }
            };

            var text = info.ToString();
            Assert.Contains("Query Parameters:", text);
            Assert.Contains("q:", text);
            Assert.Contains("limit:", text);
        }

        [Fact]
        public void StreamingApiRequestInfo_ToString_WithBody_IncludesBodyPresent()
        {
            // Lines 400-404
            var info = new StreamingApiRequestInfo
            {
                Method = "POST",
                Url = "https://example.test",
                HasBody = true
            };

            var text = info.ToString();
            Assert.Contains("Body: [PRESENT]", text);
        }

        [Fact]
        public void StreamingApiRequestInfo_ToString_Empty_MinimalOutput()
        {
            // Test empty state
            var info = new StreamingApiRequestInfo
            {
                Method = "GET",
                Url = "https://example.test"
            };

            var text = info.ToString();
            Assert.Contains("GET", text);
            Assert.Contains("https://example.test", text);
            Assert.DoesNotContain("Headers:", text);
            Assert.DoesNotContain("Query Parameters:", text);
            Assert.DoesNotContain("Body:", text);
            Assert.DoesNotContain("Policy:", text);
            Assert.DoesNotContain("Timeout:", text);
        }

        #endregion
    }
}
