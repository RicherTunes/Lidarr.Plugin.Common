using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Security;
using Lidarr.Plugin.Common.Utilities;
using Lidarr.Plugin.Common.Base;

namespace Lidarr.Plugin.Common.Services
{
    public class StreamingIndexerMixin
    {
        private readonly string _serviceName;
        private readonly StreamingCacheHelper _cache;

        public StreamingIndexerMixin(string serviceName, StreamingCacheHelper cache = null)
        {
            _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
            _cache = cache;
        }

        /// <summary>
        /// Gets cached search results if available.
        /// </summary>
        public List<StreamingSearchResult> GetCachedResults(string searchTerm, Dictionary<string, string> parameters = null)
        {
            if (_cache == null) return null;

            var cacheParams = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>())
            {
                ["searchTerm"] = searchTerm
            };

            return _cache.Get<List<StreamingSearchResult>>("search", cacheParams);
        }

        /// <summary>
        /// Caches search results for future use.
        /// </summary>
        public void CacheResults(string searchTerm, List<StreamingSearchResult> results, TimeSpan duration, Dictionary<string, string> parameters = null)
        {
            if (_cache == null || results == null) return;

            var cacheParams = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>())
            {
                ["searchTerm"] = searchTerm
            };

            _cache.Set("search", cacheParams, results, duration);
        }

        /// <summary>
        /// Converts streaming results to properties dictionaries for flexible ReleaseInfo creation.
        /// Avoids type dependency issues while providing all necessary data.
        /// </summary>
        public List<Dictionary<string, object>> ConvertToReleaseProperties(List<StreamingSearchResult> results)
        {
            return results?.Select(r => LidarrIntegrationHelpers.CreateReleaseProperties(r, _serviceName))
                          .ToList() ?? new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Validates search criteria before making API calls.
        /// </summary>
        public (bool isValid, string errorMessage) ValidateSearch(string artist, string album, string searchTerm)
        {
            return LidarrIntegrationHelpers.ValidateSearchRequest(artist, album, searchTerm);
        }

        /// <summary>
        /// Builds search URL with parameters using shared utilities.
        /// </summary>
        public string BuildSearchUrl(string baseUrl, string endpoint, Dictionary<string, string> parameters)
        {
            return StreamingIndexerHelpers.BuildSearchUrl(baseUrl, endpoint, parameters);
        }

        /// <summary>
        /// Creates standard headers for streaming service requests.
        /// </summary>
        public Dictionary<string, string> CreateHeaders(string userAgent, string authToken = null)
        {
            return StreamingIndexerHelpers.CreateStreamingHeaders(userAgent, authToken);
        }
    }

    // Note: StreamingDownloadMixin, DownloadJobInfo, and StreamingAuthMixin were
    // removed (2026-05-10). All three had zero ecosystem consumers verified by
    // ripgrep across common's src+tests and all 4 plugin repos (tidalarr,
    // qobuzarr, applemusicarr, brainarr). The download-job-tracking shape was
    // never adopted — each plugin tracks jobs in its own download-client
    // implementation. Session caching is now handled by per-plugin
    // ITokenStore<T> + OAuthStreamingAuthenticationService<T,Creds> patterns.
    //
    // StreamingIndexerMixin (above) is kept because qobuzarr's QobuzIndexer
    // still consumes its search-cache helpers.
}