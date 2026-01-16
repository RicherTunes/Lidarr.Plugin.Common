using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    public sealed class StringTokenProtector : IStringProtector
    {
        private const string Prefix = "lpc:ps:v1:";
        private readonly ITokenProtector _tokenProtector;

        public StringTokenProtector(ITokenProtector tokenProtector)
        {
            _tokenProtector = tokenProtector ?? throw new ArgumentNullException(nameof(tokenProtector));
        }

        public bool IsProtected(string? value)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
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

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = _tokenProtector.Protect(plaintextBytes);

            var algorithmIdBytes = Encoding.UTF8.GetBytes(_tokenProtector.AlgorithmId ?? string.Empty);
            var algB64Url = Base64UrlEncode(algorithmIdBytes);
            var payloadB64Url = Base64UrlEncode(protectedBytes);

            return $"{Prefix}{algB64Url}:{payloadB64Url}";
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

            var remainder = protectedValue!.Substring(Prefix.Length);
            var parts = remainder.Split(new[] { ':' }, 2, StringSplitOptions.None);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                plaintext = null;
                return false;
            }

            try
            {
                var protectedBytes = Base64UrlDecode(parts[1]);
                var unprotectedBytes = _tokenProtector.Unprotect(protectedBytes);
                plaintext = Encoding.UTF8.GetString(unprotectedBytes);
                return true;
            }
            catch
            {
                plaintext = null;
                return false;
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
