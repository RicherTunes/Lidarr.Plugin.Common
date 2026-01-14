using System;
using System.Globalization;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    public sealed class StringTokenProtector : IStringTokenProtector
    {
        private const string Prefix = "enc";
        private const string Version = "v1";

        private readonly ITokenProtector _protector;

        public StringTokenProtector(ITokenProtector protector)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
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

            if (plaintext.StartsWith(Prefix + ":", StringComparison.Ordinal))
            {
                EnsureProtectedStringIsValidForCurrentProtector(plaintext);
                return plaintext;
            }

            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = _protector.Protect(bytes);
            var payload = Convert.ToBase64String(protectedBytes);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Prefix}:{Version}:{_protector.AlgorithmId}:{payload}");
        }

        public string? Unprotect(string? protectedValue)
        {
            if (protectedValue is null)
            {
                return null;
            }

            if (protectedValue.Length == 0)
            {
                return string.Empty;
            }

            if (!protectedValue.StartsWith($"{Prefix}:", StringComparison.Ordinal))
            {
                return protectedValue;
            }

            var parts = protectedValue.Split(':', 4, StringSplitOptions.None);
            if (parts.Length != 4)
            {
                throw new FormatException("Invalid protected string format.");
            }

            var prefix = parts[0];
            var version = parts[1];
            var algorithmId = parts[2];
            var payload = parts[3];

            if (!string.Equals(prefix, Prefix, StringComparison.Ordinal))
            {
                throw new FormatException("Invalid protected string format.");
            }

            if (!string.Equals(version, Version, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"Unsupported protected string version '{version}'.");
            }

            if (!string.Equals(algorithmId, _protector.AlgorithmId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Protected string algorithm mismatch. Expected '{_protector.AlgorithmId}', got '{algorithmId}'.");
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                throw new FormatException("Invalid protected string payload.");
            }

            var bytes = _protector.Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(bytes);
        }

        private void EnsureProtectedStringIsValidForCurrentProtector(string protectedValue)
        {
            var parts = protectedValue.Split(':', 4, StringSplitOptions.None);
            if (parts.Length != 4)
            {
                throw new FormatException("Invalid protected string format.");
            }

            var prefix = parts[0];
            var version = parts[1];
            var algorithmId = parts[2];
            var payload = parts[3];

            if (!string.Equals(prefix, Prefix, StringComparison.Ordinal))
            {
                throw new FormatException("Invalid protected string format.");
            }

            if (!string.Equals(version, Version, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"Unsupported protected string version '{version}'.");
            }

            if (!string.Equals(algorithmId, _protector.AlgorithmId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Protected string algorithm mismatch. Expected '{_protector.AlgorithmId}', got '{algorithmId}'.");
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(payload);
            }
            catch (FormatException ex)
            {
                throw new FormatException("Invalid protected string payload.", ex);
            }

            byte[] plaintextBytes;
            try
            {
                plaintextBytes = _protector.Unprotect(protectedBytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to decrypt protected string payload.", ex);
            }

            try
            {
                _ = Encoding.UTF8.GetString(plaintextBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException("Unable to decode protected string payload as UTF-8 text.", ex);
            }
        }
    }
}
