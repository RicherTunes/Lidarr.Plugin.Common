using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Builds HTTP requests for indexer operations.
    /// Bridge plugins implement this to construct requests with proper headers, auth, and query params.
    /// </summary>
    public interface IIndexerRequestBuilder
    {
        /// <summary>
        /// Builds a search request for the specified endpoint.
        /// </summary>
        /// <param name="endpoint">API endpoint path</param>
        /// <param name="queryParams">Optional query parameters</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Constructed HTTP request message</returns>
        HttpRequestMessage BuildRequest(string endpoint, Dictionary<string, string>? queryParams = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds authentication headers to a request.
        /// </summary>
        /// <param name="request">Request to modify</param>
        /// <returns>Modified request with auth headers</returns>
        HttpRequestMessage AddAuthentication(HttpRequestMessage request);
    }
}
