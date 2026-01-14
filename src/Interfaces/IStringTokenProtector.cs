using System;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Protects and unprotects sensitive string data for at-rest storage.
    /// </summary>
    public interface IStringTokenProtector
    {
        /// <summary>
        /// Protects the provided plaintext value for at-rest storage.
        /// </summary>
        string? Protect(string? plaintext);

        /// <summary>
        /// Unprotects the provided value if it is in a supported protected format.
        /// Returns the input unchanged when it is not protected.
        /// </summary>
        string? Unprotect(string? protectedValue);
    }
}
