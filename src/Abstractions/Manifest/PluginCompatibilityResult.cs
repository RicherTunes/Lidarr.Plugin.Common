using System.Collections.Generic;

namespace Lidarr.Plugin.Abstractions.Manifest
{
    /// <summary>
    /// Result of comparing a plugin manifest against the host environment.
    /// </summary>
    public sealed class PluginCompatibilityResult
    {
        private PluginCompatibilityResult(bool isCompatible, string message)
        {
            IsCompatible = isCompatible;
            Message = message;
        }

        /// <summary>
        /// True when the plugin may be safely loaded.
        /// </summary>
        public bool IsCompatible { get; }

        /// <summary>
        /// Diagnostic message explaining incompatibility or additional info.
        /// </summary>
        public string Message { get; }

        public static PluginCompatibilityResult Compatible(string message = "Compatible")
            => new(true, message);

        public static PluginCompatibilityResult Incompatible(string message)
            => new(false, message);
    }
}
