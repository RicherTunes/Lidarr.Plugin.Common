using System;
using System.IO;
using System.Runtime.InteropServices;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// OS-aware case sensitivity tests for <see cref="PathTraversalGuard.IsPathWithinRoot"/>.
///
/// Case-insensitive matching universally (the pre-fix state) lets an attacker on Linux
/// escape via a case-twin: <c>/mnt/Music</c> root accepting output under <c>/mnt/music</c>
/// — two distinct directories on Linux. The guard now matches the underlying filesystem
/// (case-sensitive on Linux, case-insensitive on Windows/macOS).
/// </summary>
public class PathTraversalGuardCaseSensitivityTests
{
    [Fact]
    public void IsPathWithinRoot_CaseTwin_BehaviorMatchesFilesystem()
    {
        var root = "/mnt/Music";
        var caseTwin = "/mnt/music/album";

        var result = PathTraversalGuard.IsPathWithinRoot(caseTwin, root);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: case-sensitive filesystem → these are distinct directories, must be rejected.
            Assert.False(result, "Linux must reject case-twin path as outside root.");
        }
        else
        {
            // Windows/macOS: case-insensitive filesystem → same directory, accepted.
            Assert.True(result, "Windows/macOS must accept case-twin as inside root.");
        }
    }

    [Fact]
    public void IsPathWithinRoot_SiblingPrefix_AlwaysRejected()
    {
        // /foo/music vs /foo/musicEvil — Evil is NOT a child of music. The DirectorySeparator
        // suffix on the comparison guarantees we don't match prefix-only.
        var root = Path.Combine(Path.GetTempPath(), "ptg-prefix-music");
        var siblingEvil = root + "Evil" + Path.DirectorySeparatorChar + "album";

        Assert.False(PathTraversalGuard.IsPathWithinRoot(siblingEvil, root));
    }

    [Fact]
    public void IsPathWithinRoot_SameRootDifferentCase_BehaviorMatchesFilesystem()
    {
        // Direct equality (no descendant traversal) — same rule applies.
        var root = "/data/Library";
        var caseDifferent = "/data/library";

        var result = PathTraversalGuard.IsPathWithinRoot(caseDifferent, root);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.False(result);
        }
        else
        {
            Assert.True(result);
        }
    }
}
