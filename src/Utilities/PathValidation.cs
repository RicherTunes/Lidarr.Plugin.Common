using System;
using System.IO;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Minimal, cross-platform path sanity checks for plugins/CLIs.
/// Avoids host dependencies and overly strict behaviors.
/// </summary>
public static class PathValidation
{
    /// <summary>
    /// Returns true if the path has no invalid characters and has a non-empty root.
    /// Intended as a permissive sanity check, not a security gate.
    /// </summary>
    public static bool IsReasonablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root);
        }
        catch
        {
            return false;
        }
    }
}
