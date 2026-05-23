using System;
using System.IO;
using System.Runtime.InteropServices;
using Lidarr.Plugin.Common.Security;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Security;

/// <summary>
/// COM-005: Regression suite for IsSafePath bypass vectors.
///
/// Tests are grouped into two categories:
///   BYPASS — paths that must be REJECTED (were previously accepted due to missing normalisation)
///   ACCEPTED — well-formed paths that must continue to PASS (regression guards)
///
/// See docs/SECURITY/COM-005-COM-011-REMEDIATION.md for the full threat model.
/// </summary>
public sealed class SanitizePathBypassTests
{
    // ──────────────────────────────────────────────────────────────
    // BYPASS: Mixed separator traversal  foo/..\..\bar
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_RejectsMixedSeparatorTraversal()
    {
        // Mixed forward/back slashes with ".." that resolve outside root when normalised.
        Assert.False(Sanitize.IsSafePath(@"foo/..\..\ bar"));
        Assert.False(Sanitize.IsSafePath(@"foo/..\..\etc\passwd"));
    }

    // ──────────────────────────────────────────────────────────────
    // BYPASS: Percent-encoded dot-dot  foo/%2e%2e/bar
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_RejectsPercentEncodedDotDot()
    {
        Assert.False(Sanitize.IsSafePath("foo/%2e%2e/bar"));
        Assert.False(Sanitize.IsSafePath("foo/%2E%2E/bar"));   // upper-case hex
        Assert.False(Sanitize.IsSafePath("%2e%2e/etc/passwd"));
        Assert.False(Sanitize.IsSafePath("a/%2e./b"));         // mixed: one percent-encoded dot
    }

    // ──────────────────────────────────────────────────────────────
    // BYPASS: Unicode-normalised dots (fullwidth full stop U+FF0E)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_RejectsUnicodeNormalizedDots()
    {
        // U+FF0E FULLWIDTH FULL STOP — NFKC-normalises to U+002E (ASCII dot)
        const string fullwidthDot = "．．";                 // ．．
        Assert.False(Sanitize.IsSafePath($"foo/{fullwidthDot}/bar"));
        Assert.False(Sanitize.IsSafePath($"{fullwidthDot}/etc/passwd"));

        // U+2024 ONE DOT LEADER is a single look-alike; only ../ matters as a pair.
        // Two single-dot-leaders side by side should also be rejected.
        const string oneDotLeader = "․․";
        Assert.False(Sanitize.IsSafePath($"foo/{oneDotLeader}/bar"));
    }

    // ──────────────────────────────────────────────────────────────
    // BYPASS: Long UNC / extended-length path prefix   \\?\C:\..
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_RejectsLongUNCBypass()
    {
        // \\?\ is an extended-length path prefix that bypasses MAX_PATH but must
        // still be rejected by a traversal guard.
        Assert.False(Sanitize.IsSafePath(@"\\?\C:\.."));
        Assert.False(Sanitize.IsSafePath(@"\\?\C:\..\Windows\System32"));
        Assert.False(Sanitize.IsSafePath(@"\\?\UNC\server\share\..\.."));
    }

    // ──────────────────────────────────────────────────────────────
    // BYPASS: Null-byte / control-character injection
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_RejectsNullByteInPath()
    {
        Assert.False(Sanitize.IsSafePath("foo\0/etc/passwd"));
        Assert.False(Sanitize.IsSafePath("foo\0.."));
    }

    // ──────────────────────────────────────────────────────────────
    // REGRESSION GUARDS: well-formed paths must still be ACCEPTED
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_AcceptsNullOrWhitespace()
    {
        // Original contract: null/whitespace returns true (treated as "no path = safe").
        Assert.True(Sanitize.IsSafePath(null));
        Assert.True(Sanitize.IsSafePath(""));
        Assert.True(Sanitize.IsSafePath("   "));
    }

    [Fact]
    public void IsSafePath_AcceptsSimpleRelativePath()
    {
        Assert.True(Sanitize.IsSafePath("music"));
        Assert.True(Sanitize.IsSafePath("artist/album/track.flac"));
        Assert.True(Sanitize.IsSafePath("Artist Name/Album Title/01 - Track.flac"));
    }

    [Fact]
    public void IsSafePath_AcceptsPathWithSpacesAndUnicode()
    {
        Assert.True(Sanitize.IsSafePath("Björk/Homogenic/01 - Hunter.flac"));
        Assert.True(Sanitize.IsSafePath("音楽/アルバム/曲.flac"));
        Assert.True(Sanitize.IsSafePath("AC/DC - Back in Black"));
    }

    [Fact]
    public void IsSafePath_AcceptsPathWithDotsInFilename()
    {
        // Single dots and dots within filenames are NOT traversal.
        Assert.True(Sanitize.IsSafePath("./music"));
        Assert.True(Sanitize.IsSafePath("artist/album.deluxe/track.flac"));
        Assert.True(Sanitize.IsSafePath("v1.0/release/audio.m4a"));
    }

    [Fact]
    public void IsSafePath_AcceptsAbsoluteRootedPaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.True(Sanitize.IsSafePath(@"C:\Music\Artist\Album\track.flac"));
            Assert.True(Sanitize.IsSafePath(@"D:\Downloads\qobuz\artist\album"));
        }
        else
        {
            Assert.True(Sanitize.IsSafePath("/home/user/music/album/track.flac"));
            Assert.True(Sanitize.IsSafePath("/tmp/lidarr/downloads"));
        }
    }

    [Fact]
    public void IsSafePath_AcceptsHyphenatedAndSpecialCharacters()
    {
        // Common in music paths — these must not be rejected
        Assert.True(Sanitize.IsSafePath("AC-DC/Back In Black/01 - Hells Bells.flac"));
        Assert.True(Sanitize.IsSafePath("artist (2023)/album [Explicit]/track.m4a"));
        Assert.True(Sanitize.IsSafePath("Céline Dion/Greatest Hits/01.flac"));
    }

    // ──────────────────────────────────────────────────────────────
    // EDGE CASES: paths containing ".." as a substring of a name
    // (e.g., "..xyz" or "abc..def") — these should be ACCEPTED
    // because they don't form a traversal segment.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsSafePath_AcceptsDoubleDotAsSubstringOfFilename()
    {
        // "..txt" as a filename component is odd but not a traversal.
        // We only care about ".." as a complete path segment.
        Assert.True(Sanitize.IsSafePath("artist/album/..note.txt"));
        Assert.True(Sanitize.IsSafePath("artist/my..album/track.flac"));
    }
}
