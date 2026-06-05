using System;
using System.IO;
using Lidarr.Plugin.Common.HostBridge;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>Outcome of a <see cref="SafeDirectoryCleanup"/> operation.</summary>
    public readonly record struct DirectoryCleanupResult(bool Deleted, string? Reason)
    {
        /// <summary>The tree was deleted.</summary>
        public static DirectoryCleanupResult Removed { get; } = new(true, null);

        /// <summary>Nothing was deleted and that is fine (e.g. the path did not exist) — <see cref="Reason"/> is null.</summary>
        public static DirectoryCleanupResult Noop { get; } = new(false, null);

        /// <summary>The operation was refused by policy; <see cref="Reason"/> explains why.</summary>
        public static DirectoryCleanupResult Refused(string reason) => new(false, reason);
    }

    /// <summary>
    /// Root-contained recursive directory deletion for failed-download cleanup. Plugins assemble downloads
    /// under a configured root and, on failure, delete the partial tree — but if the path handed to cleanup is
    /// hostile or mis-derived (a traversal, the root itself, or an unrelated directory), a naive recursive
    /// delete becomes a data-loss primitive. This guard refuses to delete the root itself or anything that does
    /// not resolve to a strict descendant of the root (canonical-form check via <see cref="PathTraversalGuard"/>,
    /// which resolves <c>..</c> and defends sibling-prefix + case-twin escapes).
    /// </summary>
    public static class SafeDirectoryCleanup
    {
        /// <summary>
        /// Recursively deletes the directory tree at <paramref name="path"/> only when it is a strict descendant
        /// of <paramref name="root"/>. Refuses (without deleting) when either argument is empty, when
        /// <paramref name="path"/> equals or resolves outside <paramref name="root"/>. A non-existent in-bounds
        /// path is a no-op, not a refusal.
        /// </summary>
        public static DirectoryCleanupResult DeleteTreeUnderRoot(string? path, string? root)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DirectoryCleanupResult.Refused("path is empty.");
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                return DirectoryCleanupResult.Refused("root is empty.");
            }

            string canonicalPath;
            string canonicalRoot;
            try
            {
                canonicalPath = Path.GetFullPath(path);
                canonicalRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return DirectoryCleanupResult.Refused("path or root is not a valid filesystem path.");
            }

            if (string.Equals(canonicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    canonicalRoot, PathComparison))
            {
                return DirectoryCleanupResult.Refused("refusing to delete the root directory itself.");
            }

            if (!PathTraversalGuard.IsPathWithinRoot(canonicalPath, canonicalRoot))
            {
                return DirectoryCleanupResult.Refused("path resolves outside the configured root.");
            }

            if (!Directory.Exists(canonicalPath))
            {
                return DirectoryCleanupResult.Noop;
            }

            Directory.Delete(canonicalPath, recursive: true);
            return DirectoryCleanupResult.Removed;
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }
}
