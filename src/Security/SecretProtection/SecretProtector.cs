using System;
using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.SecretProtection
{
    /// <summary>
    /// Versioned string envelope over <see cref="ITokenProtector"/>.
    /// </summary>
    public sealed class SecretProtector : ISecretProtector
    {
        private const string V1Prefix = "enc:v1:";
        private const string V2Prefix = "enc:v2:";

        private readonly ITokenProtector _tokenProtector;
        private readonly byte[]? _legacyAesGcmKey;

        public SecretProtector(ITokenProtector tokenProtector, byte[]? legacyAesGcmKey = null)
        {
            _tokenProtector = tokenProtector ?? throw new ArgumentNullException(nameof(tokenProtector));
            _legacyAesGcmKey = legacyAesGcmKey;
        }

        public string Protect(string? plaintext)
        {
            if (string.IsNullOrWhiteSpace(plaintext)) return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = _tokenProtector.Protect(bytes);
            var payload = Convert.ToBase64String(protectedBytes);

            return $"{V2Prefix}{_tokenProtector.AlgorithmId}:{payload}";
        }

        public string Unprotect(string? protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue)) return string.Empty;

            if (protectedValue.StartsWith(V2Prefix, StringComparison.Ordinal))
            {
                return UnprotectV2(protectedValue);
            }

            if (protectedValue.StartsWith(V1Prefix, StringComparison.Ordinal))
            {
                return UnprotectV1(protectedValue);
            }

            if (protectedValue.StartsWith("enc:", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return protectedValue;
        }

        private string UnprotectV2(string protectedValue)
        {
            var remainder = protectedValue.Substring(V2Prefix.Length);
            var colonIndex = remainder.IndexOf(':');
            if (colonIndex <= 0) return string.Empty;

            var payload = remainder.Substring(colonIndex + 1);
            if (string.IsNullOrWhiteSpace(payload)) return string.Empty;

            try
            {
                var protectedBytes = Convert.FromBase64String(payload);
                var bytes = _tokenProtector.Unprotect(protectedBytes);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string UnprotectV1(string protectedValue)
        {
            if (_legacyAesGcmKey == null || _legacyAesGcmKey.Length != 32) return string.Empty;

            var payload = protectedValue.Substring(V1Prefix.Length);

            try
            {
                var parts = payload.Split('.');
                if (parts.Length != 3) return string.Empty;

                var nonce = Convert.FromBase64String(parts[0]);
                var cipher = Convert.FromBase64String(parts[1]);
                var tag = Convert.FromBase64String(parts[2]);

                var plaintext = new byte[cipher.Length];
                using var aes = new AesGcm(_legacyAesGcmKey, 16);
                aes.Decrypt(nonce, cipher, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}

