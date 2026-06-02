using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

// Common-side coverage for the shared PreviewDetectionUtility (consumed by qobuzarr's
// StreamUrlProvider/QobuzStreamAvailabilityService + QobuzCLI). The qobuz plugin used to
// carry a diverged fork tested locally; the fork was removed (qobuzarr #311) and these
// tests now own the contract ecosystem-wide, including the richer overloads the fork lacked.
public class PreviewDetectionUtilityTests
{
    [Theory]
    [InlineData("https://stream.example.com/track_preview_123456.mp3", true)]
    [InlineData("https://stream.example.com/track_sample_123456.mp3", true)]
    [InlineData("https://stream.example.com/preview/track123.flac", true)]
    [InlineData("https://stream.example.com/sample/track123.flac", true)]
    [InlineData("https://stream.example.com/track.mp3?preview=true", true)]
    [InlineData("https://stream.example.com/track.mp3?sample=1", true)]
    [InlineData("https://stream.example.com/track_demo_version.mp3", true)]
    [InlineData("https://stream.example.com/track_30sec_version.mp3", true)]
    [InlineData("https://stream.example.com/track_30s_version.mp3", true)]
    [InlineData("https://stream.example.com/track.mp3?duration=30", true)]
    [InlineData("https://stream.example.com/clip_track123.mp3", true)]
    [InlineData("https://stream.example.com/track_clip_123.mp3", true)]
    [InlineData("https://stream.example.com/track_short_version.mp3", true)]
    [InlineData("https://stream.example.com/track_excerpt_123.mp3", true)]
    [InlineData("https://stream.example.com/track_teaser_123.mp3", true)]
    [InlineData("https://stream.example.com/track_snippet_123.mp3", true)]
    [InlineData("https://stream.example.com/track123456.mp3", false)]
    [InlineData("https://stream.example.com/full_track.flac", false)]
    [InlineData("https://stream.example.com/track.mp3?quality=27", false)]
    public void IsPreviewOrSampleUrl_DetectsPatterns(string url, bool expected)
    {
        Assert.Equal(expected, PreviewDetectionUtility.IsPreviewOrSampleUrl(url));
    }

    [Fact]
    public void IsPreviewOrSampleUrl_NullOrWhitespace_IsFalse()
    {
        Assert.False(PreviewDetectionUtility.IsPreviewOrSampleUrl(null!));
        Assert.False(PreviewDetectionUtility.IsPreviewOrSampleUrl(""));
        Assert.False(PreviewDetectionUtility.IsPreviewOrSampleUrl("   "));
    }

    [Fact]
    public void IsPreviewOrSampleUrl_IsCaseInsensitive()
    {
        Assert.True(PreviewDetectionUtility.IsPreviewOrSampleUrl("https://example.com/PREVIEW_track.mp3"));
        Assert.True(PreviewDetectionUtility.IsPreviewOrSampleUrl("https://example.com/Preview_track.mp3"));
        Assert.True(PreviewDetectionUtility.IsPreviewOrSampleUrl("https://example.com/pReViEw_track.mp3"));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    [InlineData(29, false)]
    [InlineData(31, false)]
    [InlineData(120, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsPreviewDuration_MatchesKnownPreviewLimits(int seconds, bool expected)
    {
        Assert.Equal(expected, PreviewDetectionUtility.IsPreviewDuration(seconds));
    }

    [Theory]
    [InlineData(30, 90, true)]
    [InlineData(90, 90, true)]
    [InlineData(91, 90, false)]
    [InlineData(45, 30, false)]
    [InlineData(1, 1, true)]
    [InlineData(0, 90, false)]
    [InlineData(-5, 90, false)]
    public void IsPreviewDuration_WithThreshold_TreatsUpToThresholdAsPreview(int seconds, int threshold, bool expected)
    {
        Assert.Equal(expected, PreviewDetectionUtility.IsPreviewDuration(seconds, threshold));
    }

    [Theory]
    [InlineData("https://example.com/preview.mp3", null, null, true)]
    [InlineData("https://example.com/track.mp3", 30, null, true)]
    [InlineData(null, null, "This is a preview version", true)]
    [InlineData(null, null, "Sample track only", true)]
    [InlineData(null, null, "30 second excerpt available", true)]
    [InlineData(null, null, "Short clip", true)]
    [InlineData("https://example.com/track.mp3", 180, "Full track", false)]
    [InlineData("https://example.com/track.mp3", null, null, false)]
    [InlineData(null, 180, null, false)]
    [InlineData(null, null, "Full version available", false)]
    public void IsLikelyPreview_CombinesUrlDurationAndMessage(string? url, int? duration, string? message, bool expected)
    {
        Assert.Equal(expected, PreviewDetectionUtility.IsLikelyPreview(url!, duration, message!));
    }

    [Theory]
    [InlineData("https://example.com/stream.m3u8", true)]
    [InlineData("https://example.com/samples/track.mp3", true)]
    [InlineData("https://example.com/clip/track.mp3", true)]
    [InlineData("https://example.com/snippet/track.mp3", true)]
    [InlineData("https://example.com/trial/track.mp3", true)]
    [InlineData("https://example.com/full/track.mp3", false)]
    public void IsLikelyPreview_Extended_MatchesExtraUrlPatterns(string url, bool expected)
    {
        // The extra patterns (.m3u8, /samples/, /clip/, /snippet/, /trial/) live only in the
        // 5-arg overload — the plain IsPreviewOrSampleUrl does NOT match them.
        Assert.Equal(expected, PreviewDetectionUtility.IsLikelyPreview(url, durationSeconds: null, restrictionMessage: null!, durationThresholdSeconds: 90));
    }

    [Fact]
    public void IsLikelyPreview_Extended_HonorsCustomThreshold()
    {
        Assert.True(PreviewDetectionUtility.IsLikelyPreview(url: null!, durationSeconds: 120, restrictionMessage: null!, durationThresholdSeconds: 120));
        Assert.False(PreviewDetectionUtility.IsLikelyPreview(url: null!, durationSeconds: 120, restrictionMessage: null!, durationThresholdSeconds: 90));
    }

    [Fact]
    public void IsLikelyPreview_Extended_HonorsCallerSuppliedExtraPatterns()
    {
        Assert.True(PreviewDetectionUtility.IsLikelyPreview(
            url: "https://example.com/track-taster.mp3",
            durationSeconds: null,
            restrictionMessage: null!,
            durationThresholdSeconds: 90,
            extraPatterns: new[] { "-taster" }));
    }

    [Fact]
    public void GetPreviewMessage_IncludesTitleAndGuidance()
    {
        var message = PreviewDetectionUtility.GetPreviewMessage("Test Track");
        Assert.Contains("Test Track", message);
        Assert.Contains("preview/sample", message);
        Assert.Contains("Full version requires", message);
    }
}
