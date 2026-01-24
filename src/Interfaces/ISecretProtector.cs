using System;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Protects and unprotects sensitive string values for at-rest storage.
    /// </summary>
    public interface ISecretProtector
    {
        /// <summary>
        /// Protects a plaintext value, returning a versioned, opaque string envelope.
        /// Returns an empty string for null/whitespace inputs.
        /// </summary>
        string Protect(string? plaintext);

        /// <summary>
        /// Unprotects a protected value, returning plaintext.
        /// Returns an empty string when the input is null/whitespace or cannot be unprotected.
        /// </summary>
        string Unprotect(string? protectedValue);
    }
}

