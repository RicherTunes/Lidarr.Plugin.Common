using System.Diagnostics.CodeAnalysis;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Protects and unprotects sensitive strings for at-rest storage.
    /// </summary>
    public interface IStringProtector
    {
        /// <summary>
        /// Returns <c>true</c> if the value appears to be in this protector's format.
        /// </summary>
        bool IsProtected([NotNullWhen(true)] string? value);

        /// <summary>
        /// Protects plaintext for at-rest storage.
        /// </summary>
        /// <remarks>
        /// Returns <c>null</c> for <c>null</c>, returns <see cref="string.Empty" /> for empty, and is idempotent
        /// when called on an already-protected value.
        /// </remarks>
        string? Protect(string? plaintext);

        /// <summary>
        /// Unprotects a previously protected value.
        /// </summary>
        /// <remarks>
        /// If the input does not appear to be protected, it is returned as-is.
        /// </remarks>
        string? Unprotect(string? protectedValue);

        /// <summary>
        /// Attempts to unprotect a previously protected value.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the value was protected and successfully unprotected; otherwise <c>false</c>.
        /// </returns>
        bool TryUnprotect(string? protectedValue, [NotNullWhen(true)] out string? plaintext);
    }
}

