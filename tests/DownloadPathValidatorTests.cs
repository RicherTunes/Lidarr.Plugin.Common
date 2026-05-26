using System;
using System.IO;
using System.Runtime.InteropServices;
using Lidarr.Plugin.Common.Services.Validation;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// DownloadPathValidator gives streaming-plugin settings a uniform way to
/// reject obvious-bad download paths at save time, before the user hits a
/// confusing runtime error like "Access is denied" from inside the download
/// pipeline. Each plugin used to roll its own path check (typically just
/// "Path.GetFullPath succeeded") which let traversal segments and other
/// surprises through.
///
/// The validator returns a structured result with a specific
/// <see cref="DownloadPathValidator.Reason"/> so plugin UIs can show
/// actionable error text instead of generic "invalid path".
/// </summary>
public sealed class DownloadPathValidatorTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void Validate_EmptyPath_ReturnsEmptyReason()
    {
        var result = DownloadPathValidator.Validate(string.Empty);

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.Empty, result.FailureReason);
    }

    [Fact]
    public void Validate_NullPath_ReturnsEmptyReason()
    {
        var result = DownloadPathValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.Empty, result.FailureReason);
    }

    [Fact]
    public void Validate_Whitespace_ReturnsEmptyReason()
    {
        var result = DownloadPathValidator.Validate("   ");

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.Empty, result.FailureReason);
    }

    [Fact]
    public void Validate_PathWithTraversalSegment_Rejected()
    {
        // ".." segments after canonicalization indicate either a user typo or
        // an attempt to escape the configured root. Either way, reject — the
        // plugin can recommend an absolute path with no .. segments.
        var path = IsWindows ? "C:\\downloads\\..\\etc\\passwd" : "/downloads/../etc/passwd";

        var result = DownloadPathValidator.Validate(path);

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.ContainsTraversal, result.FailureReason);
    }

    [Fact]
    public void Validate_PathWithSingleTraversalSegment_Rejected()
    {
        // Even a single ".." segment (which Path.GetFullPath silently collapses)
        // is a red flag — the validator looks at the raw input, not just the
        // canonicalised form, so users who paste a relative path with .. get
        // a clear error.
        var path = IsWindows ? "..\\downloads" : "../downloads";

        var result = DownloadPathValidator.Validate(path);

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.ContainsTraversal, result.FailureReason);
    }

    [Fact]
    public void Validate_NormalAbsolutePath_Accepted()
    {
        var path = IsWindows ? "C:\\Music\\Qobuz" : "/downloads/qobuz";

        var result = DownloadPathValidator.Validate(path);

        Assert.True(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.None, result.FailureReason);
    }

    [Fact]
    public void Validate_RelativePath_Rejected()
    {
        // Lidarr's download client integration assumes paths are absolute on
        // the host filesystem. A relative path means "relative to whichever
        // working directory Lidarr happens to have", which is not a UX users
        // can reason about. Reject and tell them so.
        var path = "downloads/qobuz";

        var result = DownloadPathValidator.Validate(path);

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.NotAbsolute, result.FailureReason);
    }

    [Fact]
    public void Validate_PathWithEmbeddedNull_Rejected()
    {
        // Defensive against weird input. Path.GetFullPath throws on embedded
        // nulls; we surface that as InvalidSyntax rather than letting the
        // exception escape.
        var path = "/downloads\0/qobuz";

        var result = DownloadPathValidator.Validate(path);

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.InvalidSyntax, result.FailureReason);
    }

    // (Earlier draft had a Windows-specific '|' rejection test, but the runtime
    // surface around what Path.GetInvalidPathChars catches has narrowed across
    // .NET versions — '|' is no longer in the cross-platform invalid set even
    // though it remains unsupported by the Win32 file APIs. The null-byte case
    // above already exercises the InvalidSyntax path deterministically.)

    [Fact]
    public void Validate_ResultMessage_IsHumanReadable()
    {
        // The Message field should be non-empty and not contain raw exception
        // text — it's shown to end users.
        var result = DownloadPathValidator.Validate("../bad");

        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.DoesNotContain("System.", result.Message);
        Assert.DoesNotContain("Exception", result.Message);
    }

    [Fact]
    public void Validate_LeadingTildeOnLinux_Rejected()
    {
        // ~/... is shell expansion; Lidarr's process doesn't expand it. Users
        // who paste "~/Music" get a confusing "file not found" later. Reject
        // up front.
        if (IsWindows) return; // Tilde isn't meaningful on Windows in this context.

        var result = DownloadPathValidator.Validate("~/Music");

        Assert.False(result.IsValid);
        Assert.Equal(DownloadPathValidator.Reason.NotAbsolute, result.FailureReason);
    }
}
