using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Generic request signer abstraction for services that require signed parameters.
    /// Implementations should document ordering and salt/secret usage.
    /// </summary>
    public interface IRequestSigner
    {
        string Sign(IDictionary<string, string> parameters);
    }

    /// <summary>
    /// Simple MD5 concatenation signer (e.g., legacy styles):
<<<<<<< HEAD
    /// Joins sorted key=value pairs with <c>&amp;</c> and appends a secret, then MD5.
=======
    /// Joins sorted key=value pairs with <c>&amp;</c> and appends a secret, then MD5.
>>>>>>> 9d3d66119a6f3c48e407a01d4726940c2ed526ae
    /// </summary>
    public sealed class Md5ConcatSigner : IRequestSigner
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
<<<<<<< HEAD
    /// Joins sorted key=value pairs with <c>&amp;</c> and appends a secret, then MD5.
=======
    /// Joins sorted key=value pairs with <c>&amp;</c> and appends a secret, then MD5.
>>>>>>> 9d3d66119a6f3c48e407a01d4726940c2ed526ae
    /// </summary>
    public sealed class HmacSha256Signer : IRequestSigner
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

