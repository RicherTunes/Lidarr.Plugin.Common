using System.Net.Http;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Request-level signing seam for streaming APIs whose authentication is computed over the
    /// <b>fully-built</b> HTTP request (method + URL + headers + body) rather than over a flat
    /// query-parameter map.
    ///
    /// <para>
    /// This is intentionally distinct from <see cref="IRequestSigner"/>. That interface signs by
    /// mutating a <c>parameters</c> dictionary before the URL is assembled (the Qobuz/MD5
    /// <c>Sign(endpoint, parameters, appId, appSecret)</c> shape) and cannot express schemes such as
    /// Amazon's ADP RSA-SHA256, where the signature canonicalizes the final request line, a curated
    /// set of headers, and the request body, then emits its own canonical/signature headers. Both
    /// seams are additive and optional; plugins pick whichever matches their service. Common does not
    /// migrate existing <see cref="IRequestSigner"/> users onto this seam.
    /// </para>
    ///
    /// <para>
    /// <b>Crypto-out-of-Common stance:</b> Lidarr.Plugin.Common defines this seam and invokes it as the
    /// last mutation of a built request; it ships <b>no signing implementation</b>. All signing
    /// <i>math</i> — canonicalization (which request components are covered and in what order), key
    /// handling (RSA/HMAC private-key material, key IDs), digest/algorithm choice, and the names and
    /// formats of the headers that carry the canonical string and signature — is the <b>plugin's</b>
    /// responsibility. No RSA, HMAC, or other cryptography is performed in Common.
    /// </para>
    /// </summary>
    public interface IHttpRequestSigner
    {
        /// <summary>
        /// Returns <c>true</c> if requests to the given endpoint must be signed. Allows a plugin to
        /// sign only a subset of endpoints (e.g. authenticated calls) and leave the rest untouched.
        /// </summary>
        /// <param name="endpoint">The request endpoint path (as supplied to the builder), used by the
        /// plugin to decide whether signing applies.</param>
        bool RequiresSigning(string endpoint);

        /// <summary>
        /// Signs the already-built <paramref name="request"/> by mutating it in place — typically by
        /// computing a canonical string over the final method/URL/headers/body and adding the
        /// resulting canonical and signature headers.
        ///
        /// <para>
        /// Common guarantees this is invoked as the <b>last</b> mutation before the request is returned
        /// from the builder (URL, headers, and content are all final), so the implementation may safely
        /// canonicalize over the complete request. The signing math (canonicalization, key handling,
        /// header names/formats) lives entirely in the implementing plugin; Common contributes none of
        /// it.
        /// </para>
        /// </summary>
        /// <param name="request">The fully-constructed request to sign in place.</param>
        void Sign(HttpRequestMessage request);
    }
}
