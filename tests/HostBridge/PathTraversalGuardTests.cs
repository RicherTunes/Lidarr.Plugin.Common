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

    [Theory]
    [InlineData("Radiohead", "A Moon Shaped Pool")]
    [InlineData("Pink Floyd", "The Dark Side of the Moon")]
    public void IsPathWithinRoot_RootHasTrailingSeparator_ChildStillWithin(string artist, string album)
    {
        // Regression: users routinely configure a DownloadPath WITH a trailing separator
        // (e.g. "/downloads/qobuz/"). Path.GetFullPath PRESERVES the trailing separator, so the
        // naive "rootCanonical + separator" StartsWith check produced a DOUBLE separator
        // ("/downloads/qobuz//") and rejected every legitimate child — which blocked ALL
        // tidalarr/qobuzarr downloads ("refusing to build output path ... resolves outside the
        // configured DownloadPath"). The guard must normalize trailing separators on the root.
        var root = RandomRoot() + Path.DirectorySeparatorChar; // trailing separator (user-configured)
        var output = Path.Combine(root, artist, album);
        Assert.True(PathTraversalGuard.IsPathWithinRoot(output, root));
    }

    [Fact]
    public void IsPathWithinRoot_RootHasTrailingSeparator_EqualsRoot_ReturnsTrue()
    {
        // The bare root must still count as within a trailing-separator-configured root.
        var bare = RandomRoot();
        var rootWithSeparator = bare + Path.DirectorySeparatorChar;
        Assert.True(PathTraversalGuard.IsPathWithinRoot(bare, rootWithSeparator));
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
}
