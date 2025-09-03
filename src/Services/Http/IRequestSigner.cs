using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Generic request signer abstraction for streaming APIs.
    /// Additive and optional; concrete plugins may continue to use their own signers.
    /// </summary>
    public interface IRequestSigner
    {
        /// <summary>
        /// Returns true if the given endpoint requires signing.
        /// </summary>
        bool RequiresSigning(string endpoint);

        /// <summary>
        /// Adds signature parameters to the request if required.
        /// </summary>
        void Sign(string endpoint, IDictionary<string, string> parameters, string appId, string appSecret);
    }
}

