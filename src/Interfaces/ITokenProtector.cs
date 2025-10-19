using System;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Protects and unprotects sensitive data for at-rest storage.
    /// Implementations should provide authenticated encryption (AEAD) semantics.
    /// </summary>
    public interface ITokenProtector
    {
        /// <summary>
        /// A short, stable algorithm/provider identifier (e.g., "dpapi-user", "dataprotection").
        /// </summary>
        string AlgorithmId { get; }

        /// <summary>
        /// Protects plaintext bytes producing ciphertext bytes.
        /// </summary>
        byte[] Protect(ReadOnlySpan<byte> plaintext);

        /// <summary>
        /// Unprotects ciphertext bytes producing plaintext bytes.
        /// </summary>
        byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
    }
}

