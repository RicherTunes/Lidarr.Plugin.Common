using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Defense-in-depth helpers for keeping a plugin's download output under its configured root.
///
/// <para><b>Threat model and limitations:</b> the guard performs LEXICAL canonicalization
/// via <c>Path.GetFullPath</c>, which resolves <c>.</c>/<c>..</c>/separator quirks but does
/// NOT resolve symlinks (Linux), junctions (Windows), or NTFS reparse points. If an attacker
/// can plant a symlink inside the configured download root pointing to <c>/etc</c>, the
/// guard accepts paths under that symlink even though the resolved physical target escapes
/// the root. This is acceptable when the threat model is "hostile metadata source can inject
/// only string segments" (artist/album names from MusicBrainz / search results / manual
/// artist creation in Lidarr's UI) — those threats can't write filesystem symlinks. If your
/// threat model includes a writable-inside-root attacker, add an explicit symlink resolution
/// step at the call site (e.g. <c>new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)</c>
/// on .NET 6+).</para>
///
/// <para>Lidarr-native bridge plugins (apple/tidalarr/qobuzarr) build per-download paths by
/// <c>Path.Combine(downloadRoot, artistName, albumTitle)</c>. The artist/album segments come
/// from a metadata source (MusicBrainz, search results) that's normally trusted — but Lidarr
/// also allows manual artist creation, and a hostile search-result feed could inject a
/// segment like <c>"../../"</c> that resolves OUT of the download root once
/// <c>Directory.CreateDirectory</c> + <c>Directory.Delete(recursive: true)</c> walk the path.</para>
///
/// <para>Both <see cref="SanitizeSegment"/> (per-segment scrubbing) AND
/// <see cref="IsPathWithinRoot"/> (canonical-form whole-path containment check) need to hold
/// at the call site. Either alone is incomplete: <c>Path.GetInvalidFileNameChars()</c>
/// doesn't include <c>.</c> so <c>".."</c> survives sanitization; conversely root checks
/// are only useful when the segments are already strict.</para>
///
/// <para>Originally implemented in <c>AppleMusicLidarrDownloadClient</c> (PR #130 adversarial
/// review finding #11). Lifted here so tidalarr and qobuzarr can adopt the same guard without
/// re-implementing — the same exposure exists in both.</para>
/// </summary>
public static class PathTraversalGuard
{
    /// <summary>
    /// Sanitize a single path segment by replacing invalid filename characters with
    /// underscores, then collapsing pure-dot segments (<c>.</c>, <c>..</c>, <c>...</c>) which
    /// would otherwise resolve as up-tree references during <c>Path.GetFullPath</c>.
    ///
    /// Returns <c>"_"</c> for null / empty / whitespace input (never returns a value that
    /// <c>Path.Combine</c> would interpret as a no-op or up-reference).
    /// </summary>
    public static string SanitizeSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "_";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        var sanitized = new string(chars).Trim();

        // Collapse all-dots segments. After invalid-char replacement, `..` is still two
        // valid filename characters — but `Path.GetFullPath(Path.Combine(root, ".."))`
        // resolves UP the tree. Replace with same-length underscores so the segment retains
        // a stable identity (debugging-friendly) but can't escape the root.
        if (sanitized.Length > 0 && AllDots(sanitized))
        {
            return new string('_', sanitized.Length);
        }
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    /// <summary>
    /// Path comparison is filesystem-case-sensitive: Linux is case-sensitive, Windows / macOS
    /// (default HFS+/APFS) are case-insensitive. Using OrdinalIgnoreCase universally would
    /// let an attacker on Linux escape via a case-twin: <c>/mnt/Music</c> root would accept
    /// output paths under <c>/mnt/music</c> as "within root" even though those are two
    /// distinct directories. This `OsAwareComparison` is the safety boundary
    /// (PR #130 review #2 finding #3).
    /// </summary>
    private static readonly StringComparison OsAwareComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Verify the canonical form of <paramref name="outputPath"/> resolves to within
    /// <paramref name="root"/> (or, optionally, <paramref name="alternateRoot"/> — used for
    /// fallback dirs like <c>%TEMP%/&lt;plugin&gt;</c> in probe-only modes that allow
    /// blank-root configurations).
    ///
    /// Comparison case-sensitivity matches the underlying filesystem (case-sensitive on Linux,
    /// case-insensitive on Windows/macOS) to prevent the case-twin escape vector.
    /// Returns true if the canonical output equals or is a descendant of the root.
    /// </summary>
    public static bool IsPathWithinRoot(string outputPath, string? root, string? alternateRoot = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        var outputCanonical = Path.GetFullPath(outputPath);

        if (!string.IsNullOrWhiteSpace(root) && IsDescendant(outputCanonical, root))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(alternateRoot) && IsDescendant(outputCanonical, alternateRoot))
        {
            return true;
        }

        return false;
    }

    private static bool IsDescendant(string canonical, string root)
    {
        var rootCanonical = Path.GetFullPath(root);
        // Path.GetFullPath PRESERVES a trailing separator, so a user-configured root like
        // "/downloads/qobuz/" stays "/downloads/qobuz/". Without trimming it, the
        // "rootCanonical + separator" prefix below becomes a DOUBLE separator
        // ("/downloads/qobuz//") that no legitimate child starts with — which previously
        // rejected every download path ("refusing to build output path ... resolves outside
        // the configured DownloadPath"). Trim trailing separators so the descendant check is
        // exact regardless of whether the configured root ends with a slash.
        rootCanonical = rootCanonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (canonical.Equals(rootCanonical, OsAwareComparison))
        {
            return true;
        }
        // Defends against sibling-prefix attack: /foo/musicEvil starting with /foo/music.
        // Appending the directory separator guarantees we only match true descendants.
        return canonical.StartsWith(rootCanonical + Path.DirectorySeparatorChar, OsAwareComparison)
            || canonical.StartsWith(rootCanonical + Path.AltDirectorySeparatorChar, OsAwareComparison);
    }

    private static bool AllDots(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '.') return false;
        }
        return true;
    }

    /// <summary>
    /// Predicate-only probe: returns true if <paramref name="input"/> contains a
    /// path-traversal attempt (literal <c>..</c> segments, URL-encoded variants).
    /// Use for input validation where you want to REJECT (or audit-log) traversal
    /// attempts without normalizing or building a real path — e.g. URL validators,
    /// text-field validators, log-redaction triggers.
    ///
    /// <para>This is the predicate sibling of <see cref="SanitizeSegment"/>. Use
    /// <see cref="SanitizeSegment"/> when you want to neutralize the traversal and
    /// keep going. Use <c>ContainsTraversalAttempt</c> when you want to REJECT the
    /// input entirely (return a 400, surface an error, log a suspicious-input alert).</para>
    ///
    /// <para>Detection covers:
    /// <list type="bullet">
    ///   <item>Literal <c>..</c> followed by <c>/</c> or <c>\</c> (most common case)</item>
    ///   <item><c>/..</c> or <c>\..</c> trailing or before another separator</item>
    ///   <item>Exact <c>..</c> with no surrounding chars</item>
    ///   <item>URL-encoded variants <c>%2e%2e</c> (any case combination)
    ///         followed by <c>/</c>, <c>\</c>, <c>%2f</c>, or <c>%5c</c></item>
    /// </list>
    /// </para>
    ///
    /// <para>NOT flagged: dots inside a name (<c>foo..bar</c>, <c>artist.album.2024</c>) —
    /// the predicate requires a separator-adjacency to count as traversal. This matches
    /// the actual escape vector (separator-bounded <c>..</c> segments resolve via
    /// <c>Path.GetFullPath</c>) without false-positiving common filename patterns.</para>
    ///
    /// Returns false for null / empty / whitespace input.
    ///
    /// <para>Wave-26 #45: lifted from a submodule-local patch (existed at apple's
    /// pinned SHA 639d573 but never made it back to canonical Common; brainarr's
    /// SecureUrlValidator + SubscriptionCredentialLoader call this method).</para>
    /// </summary>
    public static bool ContainsTraversalAttempt(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        // Literal `..` adjacent to a separator on either side.
        // The exact-`..` and trailing-`..` cases (no separator after) also count.
        if (input.Contains("../", StringComparison.Ordinal) ||
            input.Contains("..\\", StringComparison.Ordinal) ||
            input.Contains("/..", StringComparison.Ordinal) ||
            input.Contains("\\..", StringComparison.Ordinal))
        {
            return true;
        }
        // Exact `..` (no surrounding chars).
        if (input.Trim() == "..")
        {
            return true;
        }

        // URL-encoded traversal: %2e%2e (with any case) followed by a separator
        // (literal or URL-encoded). Use case-insensitive comparison for the hex.
        if (ContainsCaseInsensitive(input, "%2e%2e/") ||
            ContainsCaseInsensitive(input, "%2e%2e\\") ||
            ContainsCaseInsensitive(input, "%2e%2e%2f") ||
            ContainsCaseInsensitive(input, "%2e%2e%5c"))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsCaseInsensitive(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
