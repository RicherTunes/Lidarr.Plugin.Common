using System;
using System.IO;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// Tests for <see cref="PathTraversalGuard"/>. Defense-in-depth against hostile metadata —
/// these tests pin the contract that lets apple/tidalarr/qobuzarr stop maintaining their
/// own parallel sanitization code.
/// </summary>
public class PathTraversalGuardTests
{
    private static string RandomRoot() =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ptg-test-" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData("Pink Floyd")]
    [InlineData("AC/DC")]            // forward slash → invalid filename char
    [InlineData("\"Weird Al\" Yankovic")]
    [InlineData("Sigur Rós")]
    public void SanitizeSegment_NormalNames_PreservedOrReplaced(string raw)
    {
        var s = PathTraversalGuard.SanitizeSegment(raw);
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.DoesNotContain('/', s);
        Assert.DoesNotContain('\\', s);
    }

    [Theory]
    [InlineData("..", "__")]
    [InlineData("...", "___")]
    [InlineData(".", "_")]
    public void SanitizeSegment_PureDots_ReplacedSameLength(string raw, string expected)
    {
        Assert.Equal(expected, PathTraversalGuard.SanitizeSegment(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void SanitizeSegment_EmptyOrWhitespace_ReturnsUnderscore(string? raw)
    {
        Assert.Equal("_", PathTraversalGuard.SanitizeSegment(raw));
    }

    [Fact]
    public void SanitizeSegment_TrimsWhitespace()
    {
        Assert.Equal("Album", PathTraversalGuard.SanitizeSegment("  Album  "));
    }

    [Theory]
    [InlineData("Pink Floyd", "The Dark Side")]
    [InlineData("AC/DC", "Back in Black")]
    public void IsPathWithinRoot_NormalSegments_StayUnder(string artist, string album)
    {
        var root = RandomRoot();
        var a = PathTraversalGuard.SanitizeSegment(artist);
        var b = PathTraversalGuard.SanitizeSegment(album);
        var output = Path.Combine(root, a, b);
        Assert.True(PathTraversalGuard.IsPathWithinRoot(output, root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("..\\..\\etc")]
    [InlineData("../../../etc")]
    public void IsPathWithinRoot_SanitizedTraversal_StaysUnder(string maliciousArtist)
    {
        // SanitizeSegment + IsPathWithinRoot together: the sanitize step neutralizes the
        // traversal, the containment check is the safety net.
        var root = RandomRoot();
        var sanitizedArtist = PathTraversalGuard.SanitizeSegment(maliciousArtist);
        var output = Path.Combine(root, sanitizedArtist, "Album");
        Assert.True(PathTraversalGuard.IsPathWithinRoot(output, root));
    }

    [Fact]
    public void IsPathWithinRoot_UnsanitizedTraversal_RejectedByContainmentCheck()
    {
        // Even if a caller forgets to call SanitizeSegment, the containment check catches
        // the escape. This is the "defense in depth" property.
        var root = RandomRoot();
        var escapeAttempt = Path.Combine(root, "..", "..", "etc", "passwd");
        Assert.False(PathTraversalGuard.IsPathWithinRoot(escapeAttempt, root));
    }

    [Fact]
    public void IsPathWithinRoot_DifferentRoot_ReturnsFalse()
    {
        var root = RandomRoot();
        var elsewhere = Path.Combine(Path.GetTempPath(), "something-else-" + Guid.NewGuid().ToString("N"));
        Assert.False(PathTraversalGuard.IsPathWithinRoot(elsewhere, root));
    }

    [Fact]
    public void IsPathWithinRoot_EqualsRoot_ReturnsTrue()
    {
        // The download root itself is considered within-root (a download client writing
        // directly to the root is unusual but not invalid).
        var root = RandomRoot();
        Assert.True(PathTraversalGuard.IsPathWithinRoot(root, root));
    }

    [Fact]
    public void IsPathWithinRoot_AlternateRoot_AcceptsBothPaths()
    {
        // ProbeOnly fallback pattern: blank configured root → fall back to %TEMP%/<plugin>.
        // Both roots should be valid containers.
        var primaryRoot = RandomRoot();
        var altRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alt-" + Guid.NewGuid().ToString("N")));

        var underAlt = Path.Combine(altRoot, "Artist", "Album");
        Assert.True(PathTraversalGuard.IsPathWithinRoot(underAlt, primaryRoot, altRoot));

        var underPrimary = Path.Combine(primaryRoot, "Artist", "Album");
        Assert.True(PathTraversalGuard.IsPathWithinRoot(underPrimary, primaryRoot, altRoot));

        var orphan = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orphan-" + Guid.NewGuid().ToString("N")));
        Assert.False(PathTraversalGuard.IsPathWithinRoot(orphan, primaryRoot, altRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void IsPathWithinRoot_EmptyOutputPath_ReturnsFalse(string? output)
    {
        Assert.False(PathTraversalGuard.IsPathWithinRoot(output!, "/some/root"));
    }

    // Regression: user reported "Qobuzarr: refusing to build output path '/downloads/qobuz/Artist/Album'
    // — resolves outside the configured DownloadPath '/downloads/qobuz/'." (2026-05-24)
    // Root with trailing separator was rejected because GetFullPath on Linux preserves it,
    // then IsDescendant appended ANOTHER separator and looked for "//" which never matched.
    [Theory]
    [InlineData("/downloads/qobuz/")]
    [InlineData("/downloads/qobuz//")]   // double trailing, edge case
    [InlineData("/downloads/qobuz")]     // no trailing — control case
    public void IsPathWithinRoot_RootWithTrailingSeparator_AcceptsValidDescendant(string root)
    {
        // Use a relative-style descendant that GetFullPath will resolve consistently
        // across OSs. We construct from the (possibly trailing-slash) root so the test
        // exercises the bug regardless of how the caller normalized it.
        var descendant = Path.Combine(root.TrimEnd('/', '\\'), "Artist", "Album");
        Assert.True(PathTraversalGuard.IsPathWithinRoot(descendant, root));
    }

    [Fact]
    public void IsPathWithinRoot_OutputPathWithTrailingSeparator_AcceptsValidDescendant()
    {
        // Symmetry: caller might pass a directory-style output path with trailing slash.
        var root = RandomRoot();
        var descendant = Path.Combine(root, "Artist", "Album") + Path.DirectorySeparatorChar;
        Assert.True(PathTraversalGuard.IsPathWithinRoot(descendant, root));
    }
}
