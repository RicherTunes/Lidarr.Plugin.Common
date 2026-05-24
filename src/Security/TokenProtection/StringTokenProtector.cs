using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    public sealed class StringTokenProtector : IStringProtector
    {
        /// <summary>Envelope prefix for ciphertext from a real protection backend.</summary>
        private const string ProtectedPrefix = "lpc:ps:v1:";

        /// <summary>
        /// Envelope prefix for blobs from <see cref="NullTokenProtector"/> — plaintext
        /// (not encrypted). The prefix is intentionally DIFFERENT from
        /// <see cref="ProtectedPrefix"/> so an audit query
        /// (<c>LIKE 'lpc:ps:v1:%'</c>) does not match unprotected entries.
        /// Adversarial-review F3 (v1.9.2): previous design embedded the
        /// algorithm id <c>"null"</c> inside the same <c>lpc:ps:v1:</c>
        /// envelope where it was visually indistinguishable from a base64-encoded
        /// algorithm-id slot of a real ciphertext — operators searching for
        /// "encrypted blobs" would have treated null-mode entries as protected.
        /// </summary>
        private const string PlaintextPrefix = "lpc:plain:v1:";

        /// <summary>
        /// Algorithm id that <see cref="NullTokenProtector"/> returns. Drives
        /// the envelope-prefix decision in <see cref="Protect"/>.
        /// </summary>
        private const string NullAlgorithmId = "null";

        private readonly ITokenProtector _tokenProtector;

        public StringTokenProtector(ITokenProtector tokenProtector)
        {
            _tokenProtector = tokenProtector ?? throw new ArgumentNullException(nameof(tokenProtector));
        }

        public bool IsProtected(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                || value.StartsWith(PlaintextPrefix, StringComparison.Ordinal);
        }

        public string? Protect(string? plaintext)
        {
            if (plaintext is null)
            {
                return null;
            }

            if (plaintext.Length == 0)
            {
                return string.Empty;
            }

            if (IsProtected(plaintext))
            {
                return plaintext;
            }

            // Adversarial-review F3 fix: choose the envelope prefix based on the
            // wrapped protector's algorithm id. Real backends use the standard
            // `lpc:ps:v1:` envelope; the null fallback uses `lpc:plain:v1:` so
            // the on-disk format is unambiguously identifiable as unprotected.
            var algorithmId = _tokenProtector.AlgorithmId ?? string.Empty;
            var isNullBackend = string.Equals(algorithmId, NullAlgorithmId, StringComparison.Ordinal);
            var envelopePrefix = isNullBackend ? PlaintextPrefix : ProtectedPrefix;

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                var protectedBytes = _tokenProtector.Protect(plaintextBytes);

                var algorithmIdBytes = Encoding.UTF8.GetBytes(algorithmId);
                var algB64Url = Base64UrlEncode(algorithmIdBytes);
                var payloadB64Url = Base64UrlEncode(protectedBytes);

                return $"{envelopePrefix}{algB64Url}:{payloadB64Url}";
            }
            finally
            {
                // Zero the intermediate plaintext copy (the original `plaintext` string is immutable
                // and outside our control; this only neutralizes the byte[] we just allocated).
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }

        public string? Unprotect(string? protectedValue)
        {
            if (!IsProtected(protectedValue))
            {
                return protectedValue;
            }

            if (!TryUnprotect(protectedValue, out var plaintext))
            {
                throw new InvalidOperationException("Invalid protected string format.");
            }

            return plaintext;
        }

        public bool TryUnprotect(string? protectedValue, [NotNullWhen(true)] out string? plaintext)
        {
            if (!IsProtected(protectedValue))
            {
                plaintext = protectedValue;
                return false;
            }

            // Adversarial-review F3 fix: identify which envelope shape this is
            // so we strip the right prefix. Both shapes carry an algorithm-id
            // segment + payload after the prefix.
            string remainder;
            if (protectedValue!.StartsWith(PlaintextPrefix, StringComparison.Ordinal))
            {
                remainder = protectedValue!.Substring(PlaintextPrefix.Length);
            }
            else
            {
                remainder = protectedValue!.Substring(ProtectedPrefix.Length);
            }

            var parts = remainder.Split(new[] { ':' }, 2, StringSplitOptions.None);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                plaintext = null;
                return false;
            }

            byte[]? unprotectedBytes = null;
            try
            {
                var protectedBytes = Base64UrlDecode(parts[1]);
                unprotectedBytes = _tokenProtector.Unprotect(protectedBytes);
                plaintext = Encoding.UTF8.GetString(unprotectedBytes);
                return true;
            }
            catch
            {
                plaintext = null;
                return false;
            }
            finally
            {
                // Zero the intermediate plaintext byte[] returned by the underlying protector.
                // The decoded string is now in `plaintext` (immutable); we cannot zero that, but
                // neutralizing the byte buffer reduces residue.
                if (unprotectedBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(unprotectedBytes);
                }
            }
        }

        private static string Base64UrlEncode(byte[] input)
        {
            var base64 = Convert.ToBase64String(input);
            return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static byte[] Base64UrlDecode(string base64Url)
        {
            var padded = base64Url.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            return Convert.FromBase64String(padded);
        }
    }
}
