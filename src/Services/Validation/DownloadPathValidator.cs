using System;
using System.IO;
using System.Linq;

namespace Lidarr.Plugin.Common.Services.Validation
{
    /// <summary>
    /// Reusable validator for streaming-plugin <c>DownloadPath</c> settings.
    ///
    /// Each plugin used to roll its own check (typically just
    /// "did <see cref="System.IO.Path.GetFullPath"/> not throw?"), which let
    /// traversal segments, relative paths, and other surprises through. This
    /// validator returns a structured result with a specific
    /// <see cref="Reason"/> so plugin UIs can show actionable error text
    /// instead of generic "invalid path".
    ///
    /// Rejection rules:
    /// <list type="bullet">
    ///   <item>Empty / whitespace → <see cref="Reason.Empty"/></item>
    ///   <item>Contains <c>..</c> path segment → <see cref="Reason.ContainsTraversal"/></item>
    ///   <item>Not absolute → <see cref="Reason.NotAbsolute"/></item>
    ///   <item>OS-invalid chars / embedded null / syntactic failure → <see cref="Reason.InvalidSyntax"/></item>
    /// </list>
    ///
    /// This is purely a syntactic/semantic check on the supplied string. It
    /// does NOT touch the filesystem — directory existence, writability, and
    /// permission probes are out of scope (they belong in a separate
    /// connection-test pass so they can be slow + retryable independently).
    /// </summary>
    public static class DownloadPathValidator
    {
        public enum Reason
        {
            None,
            Empty,
            ContainsTraversal,
            NotAbsolute,
            InvalidSyntax
        }

        public readonly record struct Result(bool IsValid, Reason FailureReason, string Message)
        {
            public static Result Ok() => new(true, Reason.None, string.Empty);
            public static Result Fail(Reason reason, string message) => new(false, reason, message);
        }

        public static Result Validate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Result.Fail(Reason.Empty, "Download path is required.");
            }

            // Embedded NUL is a hard syntactic violation regardless of OS. Catch
            // it before any rooted/relative analysis so the failure mode is
            // diagnostic ("invalid chars") rather than misleading ("not absolute").
            if (path.IndexOf('\0') >= 0)
            {
                return Result.Fail(Reason.InvalidSyntax,
                    "Download path contains characters that are not allowed by the host filesystem.");
            }

            // Reject relative paths and tilde-shell-expansion up front so users
            // get a clear error instead of a confusing runtime miss when Lidarr
            // doesn't resolve them the way they expect.
            if (path.StartsWith("~", StringComparison.Ordinal))
            {
                return Result.Fail(Reason.NotAbsolute,
                    "Download path must be absolute. '~' is not expanded — use the full path.");
            }

            // Reject obvious traversal in the raw input (..\ or ../). Doing this
            // before Path.GetFullPath catches the cases where canonicalisation
            // would silently collapse them.
            if (ContainsTraversalSegment(path))
            {
                return Result.Fail(Reason.ContainsTraversal,
                    "Download path must not contain '..' segments. Use an absolute path without parent-directory references.");
            }

            // Reject relative paths on the RAW input — Path.GetFullPath happily
            // resolves them against the process working directory, which is not
            // a UX users can reason about.
            if (!Path.IsPathFullyQualified(path))
            {
                return Result.Fail(Reason.NotAbsolute,
                    "Download path must be absolute (e.g. '/downloads/qobuz' or 'C:\\Music').");
            }

            // Explicit invalid-char check — newer .NET tolerates some chars in
            // Path.GetFullPath that are actually unsupported by the underlying
            // filesystem APIs (notably '|' on Windows). Catch them up front.
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return Result.Fail(Reason.InvalidSyntax,
                    "Download path contains characters that are not allowed by the host filesystem.");
            }

            string? full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                // Path contains invalid chars or null byte
                return Result.Fail(Reason.InvalidSyntax,
                    "Download path contains characters that are not allowed by the host filesystem.");
            }
            catch (NotSupportedException)
            {
                return Result.Fail(Reason.InvalidSyntax,
                    "Download path syntax is not supported by the host filesystem.");
            }
            catch (PathTooLongException)
            {
                return Result.Fail(Reason.InvalidSyntax,
                    "Download path is too long for the host filesystem.");
            }

            // After canonicalisation, the path must be rooted/absolute.
            if (!Path.IsPathRooted(full) || !Path.IsPathFullyQualified(full))
            {
                return Result.Fail(Reason.NotAbsolute,
                    "Download path must be absolute (e.g. '/downloads/qobuz' or 'C:\\Music').");
            }

            return Result.Ok();
        }

        private static bool ContainsTraversalSegment(string path)
        {
            // Split on both forward and back slashes so cross-platform inputs
            // are handled uniformly.
            var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            return segments.Any(s => s == "..");
        }
    }
}
