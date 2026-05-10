using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Hash-based parameter signer abstraction. Implementations sort the supplied
    /// parameters deterministically and produce a single hex/base64 signature using
    /// a shared secret. Use this for legacy MD5-concat or HMAC-SHA256 schemes.
    /// </summary>
    /// <remarks>
    /// This was previously named <c>IRequestSigner</c>. The legacy name conflicted with
    /// <see cref="Services.Http.IRequestSigner"/> (the streaming-API signer) when both
    /// namespaces were imported. The legacy interface remains as <c>[Obsolete]</c> so
    /// existing plugins keep compiling; new code should target <c>IHmacSigner</c>.
    /// </remarks>
    public interface IHmacSigner
    {
        /// <summary>
        /// Signs the supplied parameter set and returns the resulting digest.
        /// </summary>
        /// <param name="parameters">Parameters participating in the signature.</param>
        /// <returns>The signature string (typically lowercase hex).</returns>
        string Sign(IDictionary<string, string> parameters);
    }

    /// <summary>
    /// Generic request signer abstraction for services that require signed parameters.
    /// Implementations should document ordering and salt/secret usage.
    /// </summary>
    [Obsolete("Use IHmacSigner. The IRequestSigner name now lives at Lidarr.Plugin.Common.Services.Http.IRequestSigner (streaming-API request signer); this hash-based signer was renamed to remove the ambiguity. This alias will remain for the 1.x line.")]
    public interface IRequestSigner : IHmacSigner
    {
    }

    /// <summary>
    /// Simple MD5 concatenation signer (e.g., legacy styles):
    /// Joins sorted key=value pairs with <c>&amp;</c> and appends a secret, then MD5.
    /// </summary>
#pragma warning disable CS0618 // legacy IRequestSigner alias retained for binary/source compat
    public sealed class Md5ConcatSigner : IHmacSigner, IRequestSigner
#pragma warning restore CS0618
    {
        private readonly string _secret;
        public Md5ConcatSigner(string secret)
        {
            _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        }

        public string Sign(IDictionary<string, string> parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            var data = string.Join("&", parameters
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            var payload = data + _secret;
            return HashingUtility.ComputeMD5Hash(payload);
        }
    }

    /// <summary>
    /// HMAC-SHA256 signer: joins sorted key=value pairs with <c>&amp;</c> and signs using the secret.
    /// </summary>
#pragma warning disable CS0618 // legacy IRequestSigner alias retained for binary/source compat
    public sealed class HmacSha256Signer : IHmacSigner, IRequestSigner
#pragma warning restore CS0618
    {
        private readonly string _secret;
        public HmacSha256Signer(string secret)
        {
            _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        }

        public string Sign(IDictionary<string, string> parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            var data = string.Join("&", parameters
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            return HashingUtility.ComputeHmacSha256(_secret, data);
        }
    }
}

