using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Download;

/// <summary>
/// Pins the canonical album-completion rule shared by all streaming plugins:
/// an album download is successful ONLY when every track lands on disk
/// (successfulTracks == totalTracks). Any deficit — a failed track OR a
/// sample/preview-skipped one — leaves the album incomplete, which Lidarr's
/// NoMissingOrUnmatchedTracksSpecification permanently rejects ("Has missing
/// tracks"). So an incomplete album must report failure (→ Lidarr blocklists +
/// re-searches / falls back) rather than Completed (→ silent permanent reject).
/// The MinimumSuccessRate / TreatPreviewAsFailure knobs can only ever gate a
/// COMPLETE album; they can never rescue an incomplete one. This was a live
/// regression in qobuz (Aphex Twin – Drukqs, 29/30) and is tidal's existing rule
/// (failedTracks &gt; 0 ⇒ Failed); this is the single source of truth for both.
/// </summary>
public sealed class AlbumCompletionPolicyTests
{
    [Theory]
    // Hard gate: ANY missing track ⇒ incomplete ⇒ NOT successful, regardless of rate.
    [InlineData(30, 29, 0, false)] // Drukqs: 29/30 = 96.7% ≥ 80% but one track missing ⇒ fail
    [InlineData(10, 8, 0, false)]  // 2 failed ⇒ incomplete ⇒ fail (even at exactly 80%)
    [InlineData(10, 9, 0, false)]  // 1 failed ⇒ incomplete ⇒ fail
    [InlineData(10, 5, 5, false)]  // 5 downloaded + 5 sample-skipped ⇒ 5 missing ⇒ fail
    // Complete album ⇒ successful.
    [InlineData(10, 10, 0, true)]
    [InlineData(1, 1, 0, true)]
    [InlineData(53, 53, 0, true)]
    public void IsAlbumDownloadSuccessful_GatesOnCompleteness(
        int total, int successful, int skipped, bool expected)
    {
        Assert.Equal(expected, AlbumCompletionPolicy.IsAlbumDownloadSuccessful(total, successful, skipped));
    }

    [Fact]
    public void IncompleteAlbum_IsNeverSuccessful_EvenWithPermissiveThreshold()
    {
        // A partial album is unimportable by Lidarr; a permissive rate must not rescue it.
        Assert.False(AlbumCompletionPolicy.IsAlbumDownloadSuccessful(
            totalTracks: 10, successfulTracks: 5, skippedTracks: 0, minimumSuccessRate: 0.1));
    }

    [Fact]
    public void EmptyAlbum_FollowsFailOnNoTracksAvailable()
    {
        // Default: an album with no tracks available is a failure (parity with tidal's
        // "no files on disk ⇒ Failed").
        Assert.False(AlbumCompletionPolicy.IsAlbumDownloadSuccessful(0, 0, 0));
        // Opt-out for callers that treat an empty album as a no-op success.
        Assert.True(AlbumCompletionPolicy.IsAlbumDownloadSuccessful(
            0, 0, 0, failOnNoTracksAvailable: false));
    }

    [Theory]
    // Tidal-equivalence: tidal's rule is "any failed track ⇒ Failed", i.e. success iff
    // every track succeeded. The default policy (minRate 0.8, no preview-as-failure)
    // reproduces it exactly because the hard gate fires before any rate check.
    [InlineData(12, 12, true)]
    [InlineData(12, 11, false)]
    [InlineData(12, 0, false)]
    public void DefaultPolicy_MatchesTidalAllOrNothing(int total, int successful, bool expected)
    {
        Assert.Equal(expected, AlbumCompletionPolicy.IsAlbumDownloadSuccessful(total, successful));
    }
}
