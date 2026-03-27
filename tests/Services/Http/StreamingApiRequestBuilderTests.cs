using System;
using System.Collections.Generic;
using System.Net.Http;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http
{
    [Trait("Category", "Unit")]
    public class StreamingApiRequestBuilderTests
    {
        private const string BaseUrl = "https://api.example.test/v1";

        #region Constructor Validation

        [Fact]
        public void Constructor_NullBaseUrl_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new StreamingApiRequestBuilder(null!));
        }

        [Fact]
        public void Constructor_ValidBaseUrl_Succeeds()
        {
            var builder = new StreamingApiRequestBuilder(BaseUrl);
            var request = builder.Build();

            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
        }

        [Fact]
        public void Constructor_TrailingSlash_IsTrimmed()
        {
            var builder = new StreamingApiRequestBuilder("https://api.example.test/v1/");
            var request = builder.Build();

            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
        }

        #endregion

        #region Happy Path — Build Request with Base URL, Path, Query Params

        [Fact]
        public void Build_BaseUrlOnly_ReturnsCorrectUri()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Build();

            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Get, request.Method);
        }

        [Fact]
        public void Build_WithEndpoint_AppendsTrimmedPath()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("/search/albums")
                .Build();

            Assert.Equal($"{BaseUrl}/search/albums", request.RequestUri!.ToString());
        }

        [Fact]
        public void Build_EndpointWithoutLeadingSlash_Works()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("tracks")
                .Build();

            Assert.Equal($"{BaseUrl}/tracks", request.RequestUri!.ToString());
        }

        [Fact]
        public void Build_WithQueryParams_AppendsEncoded()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("search")
                .Query("q", "hello world")
                .Query("limit", 10)
                .Build();

            // Uri.ToString() may decode percent-encoded characters, so check AbsoluteUri
            var uri = request.RequestUri!.AbsoluteUri;
            Assert.Contains("q=hello%20world", uri);
            Assert.Contains("limit=10", uri);
            Assert.Contains("?", uri);
        }

        [Fact]
        public void Build_WithBooleanQueryParam_UsesLowercaseStrings()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("search")
                .Query("explicit", true)
                .Query("clean", false)
                .Build();

            var uri = request.RequestUri!.ToString();
            Assert.Contains("explicit=true", uri);
            Assert.Contains("clean=false", uri);
        }

        #endregion

        #region HTTP Methods

        [Fact]
        public void Method_Get_IsDefault()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl).Build();
            Assert.Equal(HttpMethod.Get, request.Method);
        }

        [Fact]
        public void Method_Post_SetsCorrectly()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl).Post().Build();
            Assert.Equal(HttpMethod.Post, request.Method);
        }

        [Fact]
        public void Method_Put_SetsCorrectly()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl).Put().Build();
            Assert.Equal(HttpMethod.Put, request.Method);
        }

        [Fact]
        public void Method_Delete_SetsCorrectly()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl).Delete().Build();
            Assert.Equal(HttpMethod.Delete, request.Method);
        }

        [Fact]
        public void Method_Null_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new StreamingApiRequestBuilder(BaseUrl).Method(null!));
        }

        #endregion

        #region Header Merging

        [Fact]
        public void Header_CustomHeader_IsIncluded()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Header("X-Custom", "value123")
                .Build();

            Assert.True(request.Headers.Contains("X-Custom"));
            Assert.Contains("value123", request.Headers.GetValues("X-Custom"));
        }

        [Fact]
        public void BearerToken_SetsAuthorizationHeader()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .BearerToken("my-token")
                .Build();

            Assert.True(request.Headers.Contains("Authorization"));
            Assert.Contains("Bearer my-token", request.Headers.GetValues("Authorization"));
        }

        [Fact]
        public void ApiKey_SetsNamedHeader()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .ApiKey("X-Api-Key", "secret-key")
                .Build();

            Assert.True(request.Headers.Contains("X-Api-Key"));
            Assert.Contains("secret-key", request.Headers.GetValues("X-Api-Key"));
        }

        [Fact]
        public void Headers_MultipleDictionary_MergedCorrectly()
        {
            var headers = new Dictionary<string, string>
            {
                { "X-First", "one" },
                { "X-Second", "two" }
            };

            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Headers(headers)
                .Build();

            Assert.True(request.Headers.Contains("X-First"));
            Assert.True(request.Headers.Contains("X-Second"));
        }

        [Fact]
        public void Header_LastValueWins_WhenSameKeySetTwice()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Header("X-Custom", "first")
                .Header("X-Custom", "second")
                .Build();

            Assert.Contains("second", request.Headers.GetValues("X-Custom"));
        }

        [Fact]
        public void NoCache_SetsCacheControlHeaders()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .NoCache()
                .Build();

            Assert.True(request.Headers.Contains("Cache-Control"));
            Assert.True(request.Headers.Contains("Pragma"));
        }

        #endregion

        #region Query Parameter Encoding

        [Fact]
        public void Query_SpecialCharacters_AreEncoded()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Query("q", "rock & roll")
                .Build();

            // Use AbsoluteUri to preserve percent-encoding
            var uri = request.RequestUri!.AbsoluteUri;
            Assert.Contains("q=rock%20%26%20roll", uri);
        }

        [Fact]
        public void Query_MultipleParams_JoinedWithAmpersand()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Query("a", "1")
                .Query("b", "2")
                .Build();

            var uri = request.RequestUri!.ToString();
            Assert.Contains("a=1", uri);
            Assert.Contains("b=2", uri);
            Assert.Contains("&", uri);
        }

        [Fact]
        public void QueryParams_FromDictionary_Applied()
        {
            var parameters = new Dictionary<string, string>
            {
                { "artist", "Radiohead" },
                { "album", "OK Computer" }
            };

            var request = new StreamingApiRequestBuilder(BaseUrl)
                .QueryParams(parameters)
                .Build();

            var uri = request.RequestUri!.AbsoluteUri;
            Assert.Contains("artist=Radiohead", uri);
            Assert.Contains("album=OK%20Computer", uri);
        }

        [Fact]
        public void Query_DuplicateKeys_PreservedAsMultivalue()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Query("tag", "rock")
                .Query("tag", "alternative")
                .Build();

            var uri = request.RequestUri!.ToString();
            Assert.Contains("tag=rock", uri);
            Assert.Contains("tag=alternative", uri);
        }

        #endregion

        #region Null / Empty Input Handling

        [Fact]
        public void Endpoint_Null_UsesBaseUrlOnly()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint(null!)
                .Build();

            // Null endpoint sets _endpoint to null, so BuildUrl returns just base URL
            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
        }

        [Fact]
        public void BearerToken_NullOrEmpty_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .BearerToken(null!)
                .BearerToken("")
                .Build();

            Assert.False(request.Headers.Contains("Authorization"));
        }

        [Fact]
        public void ApiKey_NullHeaderName_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .ApiKey(null!, "key")
                .Build();

            // No extra headers added
            Assert.Empty(request.Headers);
        }

        [Fact]
        public void ApiKey_EmptyApiKey_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .ApiKey("X-Key", "")
                .Build();

            Assert.False(request.Headers.Contains("X-Key"));
        }

        [Fact]
        public void Header_NullName_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Header(null!, "value")
                .Build();

            Assert.Empty(request.Headers);
        }

        [Fact]
        public void Header_EmptyValue_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Header("X-Custom", "")
                .Build();

            Assert.False(request.Headers.Contains("X-Custom"));
        }

        [Fact]
        public void Headers_NullDictionary_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Headers(null!)
                .Build();

            Assert.Empty(request.Headers);
        }

        [Fact]
        public void Query_NullOrEmptyName_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Query(null!, "value")
                .Query("", "value")
                .Build();

            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
        }

        [Fact]
        public void Query_NullValue_DefaultsToEmpty()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Query("key", (string)null!)
                .Build();

            var uri = request.RequestUri!.ToString();
            Assert.Contains("key=", uri);
        }

        [Fact]
        public void QueryParams_NullDictionary_Ignored()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .QueryParams(null!)
                .Build();

            Assert.Equal(BaseUrl, request.RequestUri!.ToString());
        }

        #endregion

        #region Timeout Configuration

        [Fact]
        public void Timeout_SetOnBuilder_ReflectedInBuildForLogging()
        {
            var timeout = TimeSpan.FromSeconds(30);

            var builder = new StreamingApiRequestBuilder(BaseUrl)
                .Timeout(timeout);

            var info = builder.BuildForLogging();

            Assert.NotNull(info.Timeout);
            Assert.Equal(timeout, info.Timeout.Value);
        }

        [Fact]
        public void Timeout_NotSet_NullInBuildForLogging()
        {
            var builder = new StreamingApiRequestBuilder(BaseUrl);

            var info = builder.BuildForLogging();

            Assert.Null(info.Timeout);
        }

        #endregion

        #region Body Content

        [Fact]
        public void JsonBody_Post_SetsContentType()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Post()
                .JsonBody(new { title = "Test Album" })
                .Build();

            Assert.NotNull(request.Content);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        }

        [Fact]
        public void FormBody_Post_SetsFormUrlEncodedContent()
        {
            var formData = new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", "abc" }
            };

            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Post()
                .FormBody(formData)
                .Build();

            Assert.NotNull(request.Content);
            Assert.IsType<FormUrlEncodedContent>(request.Content);
        }

        [Fact]
        public void JsonBody_GetMethod_NoContentAttached()
        {
            // Body is only attached for POST/PUT
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Get()
                .JsonBody(new { q = "test" })
                .Build();

            Assert.Null(request.Content);
        }

        #endregion

        #region WithPolicy

        [Fact]
        public void WithPolicy_Null_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new StreamingApiRequestBuilder(BaseUrl).WithPolicy(null!));
        }

        [Fact]
        public void WithPolicy_Valid_ExposedViaInternalProperty()
        {
            var policy = ResiliencePolicy.Search;

            var builder = new StreamingApiRequestBuilder(BaseUrl)
                .WithPolicy(policy);

            Assert.Equal("search", builder.Policy!.Name);
        }

        [Fact]
        public void WithPolicy_ReflectedInBuildForLogging()
        {
            var policy = ResiliencePolicy.Lookup;

            var info = new StreamingApiRequestBuilder(BaseUrl)
                .WithPolicy(policy)
                .BuildForLogging();

            Assert.Equal("lookup", info.PolicyName);
        }

        #endregion

        #region BuildForLogging

        [Fact]
        public void BuildForLogging_ReturnsNonNullInfo()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("search")
                .Query("q", "test")
                .BuildForLogging();

            Assert.NotNull(info);
            Assert.Equal("GET", info.Method);
            Assert.Contains("search", info.Url);
        }

        [Fact]
        public void BuildForLogging_HasBody_IsTrueWhenBodySet()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .Post()
                .JsonBody(new { data = 1 })
                .BuildForLogging();

            Assert.True(info.HasBody);
        }

        [Fact]
        public void BuildForLogging_HasBody_IsFalseWhenNoBody()
        {
            var info = new StreamingApiRequestBuilder(BaseUrl)
                .BuildForLogging();

            Assert.False(info.HasBody);
        }

        #endregion

        #region StreamingApiRequestInfo ToString

        [Fact]
        public void StreamingApiRequestInfo_ToString_IncludesMethod()
        {
            var info = new StreamingApiRequestInfo
            {
                Method = "POST",
                Url = "https://example.test/api/search"
            };

            var text = info.ToString();
            Assert.Contains("POST", text);
            Assert.Contains("https://example.test/api/search", text);
        }

        [Fact]
        public void StreamingApiRequestInfo_ToString_IncludesTimeout()
        {
            var info = new StreamingApiRequestInfo
            {
                Method = "GET",
                Url = "https://example.test",
                Timeout = TimeSpan.FromSeconds(45)
            };

            var text = info.ToString();
            Assert.Contains("45", text);
        }

        [Fact]
        public void StreamingApiRequestInfo_ToString_IncludesPolicy()
        {
            var info = new StreamingApiRequestInfo
            {
                Method = "GET",
                Url = "https://example.test",
                PolicyName = "aggressive-retry"
            };

            var text = info.ToString();
            Assert.Contains("aggressive-retry", text);
        }

        #endregion

        #region Fluent Chaining

        [Fact]
        public void FluentChain_FullBuild_ProducesValidRequest()
        {
            var request = new StreamingApiRequestBuilder(BaseUrl)
                .Endpoint("albums/search")
                .Post()
                .BearerToken("tok123")
                .Header("Accept-Language", "en-US")
                .Query("q", "Radiohead")
                .Query("limit", 20)
                .Query("explicit", false)
                .Timeout(TimeSpan.FromSeconds(15))
                .JsonBody(new { filter = "studio" })
                .Build();

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("albums/search", request.RequestUri!.ToString());
            Assert.Contains("q=Radiohead", request.RequestUri.ToString());
            Assert.Contains("limit=20", request.RequestUri.ToString());
            Assert.Contains("explicit=false", request.RequestUri.ToString());
            Assert.True(request.Headers.Contains("Authorization"));
            Assert.True(request.Headers.Contains("Accept-Language"));
            Assert.NotNull(request.Content);
        }

        #endregion
    }
}
