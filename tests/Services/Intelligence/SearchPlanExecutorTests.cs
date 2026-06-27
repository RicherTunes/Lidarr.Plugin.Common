using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

/// <summary>
/// Characterization + contract suite for <see cref="SearchPlanExecutor"/>. The first six facts are the
/// verbatim semantics of tidalarr's <c>TidalTieredAlbumSearchTests</c> (the donor template), ported onto
/// the Common executor as the <see cref="SearchStopPolicy.StopAfterFirstTierWithResults"/> coverage. The
/// remainder pin the other two stop policies, the baked all-failed throw + service-label formatting, the
/// genuine-empty-is-not-a-failure distinction, and the cancellation contract (the fix for tidal's
/// mid-flight OperationCanceledException swallow).
/// </summary>
public sealed class SearchPlanExecutorTests
{
    private sealed record Album(string Id);

    private static IReadOnlyList<Album> WithAlbums(params string[] ids) =>
        ids.Select(id => new Album(id)).ToList();

    private static IReadOnlyList<Album> Empty() => Array.Empty<Album>();

    private static List<IReadOnlyList<string>> Tiers(params string[][] tiers) =>
        tiers.Select(t => (IReadOnlyList<string>)t).ToList();

    // ===== Ported from TidalTieredAlbumSearchTests (StopAfterFirstTierWithResults) =====

    [Fact]
    public async Task FirstTierWithResults_ShortCircuits_FallbackTiersNotAttempted()
    {
        var called = new List<string>();
        var tiers = Tiers(new[] { "combined" }, new[] { "artist-only" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(q == "combined" ? WithAlbums("a1") : Empty());
            },
            SearchStopPolicy.StopAfterFirstTierWithResults);

        Assert.Equal(new[] { "combined" }, called);
        Assert.Single(results);
        Assert.Equal("a1", results[0].Id);
    }

    [Fact]
    public async Task OnErrorThatThrows_DoesNotAbortFallback()
    {
        // A buggy onError callback (logging/auth side-effect) must never turn one failed
        // variant into an aborted fallback chain that loses the recoverable later variant.
        var called = new List<string>();
        var tiers = Tiers(new[] { "boom", "good" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                if (q == "boom")
                {
                    throw new InvalidOperationException("query failed");
                }

                return Task.FromResult(WithAlbums("a1"));
            },
            SearchStopPolicy.AccumulateAll,
            onError: (q, ex) => throw new Exception("buggy onError callback"));

        Assert.Equal(new[] { "boom", "good" }, called); // "good" still attempted
        Assert.Single(results);
        Assert.Equal("a1", results[0].Id);
    }

    [Fact]
    public async Task EmptyFirstTier_FallsBackToArtistOnlyTier()
    {
        var called = new List<string>();
        var tiers = Tiers(new[] { "combined" }, new[] { "artist-only" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(q == "artist-only" ? WithAlbums("band1", "band2") : Empty());
            },
            SearchStopPolicy.StopAfterFirstTierWithResults);

        Assert.Equal(new[] { "combined", "artist-only" }, called);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task AllVariantsInATierRun_BeforeFallingThrough()
    {
        var called = new List<string>();
        var tiers = Tiers(new[] { "v1", "v2" }, new[] { "artist-only" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(q == "v2" ? WithAlbums("hit") : Empty());
            },
            SearchStopPolicy.StopAfterFirstTierWithResults);

        // v1 empty, v2 hit -> tier produced results -> do NOT fall through to artist-only.
        Assert.Equal(new[] { "v1", "v2" }, called);
        Assert.Single(results);
    }

    [Fact]
    public async Task AllTiersEmpty_ReturnsEmpty_NoErrorSurfaced()
    {
        var tiers = Tiers(new[] { "combined" }, new[] { "artist-only" });

        var ex = await Record.ExceptionAsync(() => SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => Task.FromResult(Empty()),
            SearchStopPolicy.StopAfterFirstTierWithResults));

        Assert.Null(ex);
    }

    [Fact]
    public async Task AllRequestsThrow_SurfacesLastError_ZeroSucceeded()
    {
        var tiers = Tiers(new[] { "combined" }, new[] { "artist-only" });
        var observed = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => throw new InvalidOperationException("boom-" + q),
            SearchStopPolicy.StopAfterFirstTierWithResults,
            onError: (q, e) => observed.Add(q)));

        // Both queries attempted, both failed -> the baked all-failed contract surfaces lastError.
        Assert.Equal(new[] { "combined", "artist-only" }, observed);
        Assert.NotNull(ex.InnerException);
        Assert.Equal("boom-artist-only", ex.InnerException!.Message);
    }

    [Fact]
    public async Task ThrowingFirstTier_StillFallsBackAndRecovers()
    {
        var tiers = Tiers(new[] { "combined" }, new[] { "artist-only" });
        var observed = new List<string>();

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => q == "combined"
                ? throw new InvalidOperationException("transient")
                : Task.FromResult(WithAlbums("recovered")),
            SearchStopPolicy.StopAfterFirstTierWithResults,
            onError: (q, ex) => observed.Add(q));

        Assert.Single(results);
        Assert.Equal("recovered", results[0].Id);
        // The combined-tier failure is recorded via onError but does NOT abort the fallback.
        Assert.Equal(new[] { "combined" }, observed);
    }

    // ===== AccumulateAll (qobuz/apple) =====

    [Fact]
    public async Task AccumulateAll_IssuesEveryTier_AndMergesResults_NoEarlyStop()
    {
        var called = new List<string>();
        var tiers = Tiers(new[] { "t0" }, new[] { "t1" }, new[] { "t2" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(q == "t1" ? Empty() : WithAlbums(q));
            },
            SearchStopPolicy.AccumulateAll);

        Assert.Equal(new[] { "t0", "t1", "t2" }, called);
        Assert.Equal(new[] { "t0", "t2" }, results.Select(r => r.Id).ToArray());
    }

    // ===== StopAfterFirstVariantWithResults (amazon) =====

    [Fact]
    public async Task StopAfterFirstVariant_StopsTheInstantOneVariantReturnsResults()
    {
        var called = new List<string>();
        var tiers = Tiers(new[] { "v0", "v1" }, new[] { "v2" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(q == "v1" ? WithAlbums("hit") : Empty());
            },
            SearchStopPolicy.StopAfterFirstVariantWithResults);

        // v0 empty -> v1 hits -> stop immediately; v2 (next tier) never runs.
        Assert.Equal(new[] { "v0", "v1" }, called);
        Assert.Single(results);
        Assert.Equal("hit", results[0].Id);
    }

    // ===== Genuine empty is not a failure =====

    [Fact]
    public async Task GenuineEmpty_DoesNotThrow_ReturnsEmpty()
    {
        var tiers = Tiers(new[] { "q1", "q2" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => Task.FromResult(Empty()),
            SearchStopPolicy.AccumulateAll,
            serviceLabel: "Tidal");

        // Every variant returned (succeeded > 0) with no matches -> genuine no-results, never the throw.
        Assert.Empty(results);
    }

    [Fact]
    public async Task PartialFailure_WithSomeSuccess_DoesNotThrow()
    {
        var tiers = Tiers(new[] { "boom", "ok" });

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => q == "boom"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(WithAlbums("a")),
            SearchStopPolicy.AccumulateAll,
            serviceLabel: "Apple Music");

        Assert.Single(results);
    }

    // ===== All-failed throw + service-label formatting =====

    [Theory]
    [InlineData("Qobuz")]
    [InlineData("Apple Music")]
    [InlineData("Amazon Music")]
    [InlineData("Tidal")]
    public async Task AllFailed_Throws_WithServiceLabelInMessage(string label)
    {
        var tiers = Tiers(new[] { "q1", "q2" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => throw new InvalidOperationException("inner-" + q),
            SearchStopPolicy.AccumulateAll,
            serviceLabel: label));

        Assert.Equal(
            $"All 2 {label} request(s) failed; surfacing the error instead of an empty result.",
            ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Equal("inner-q2", ex.InnerException!.Message);
    }

    [Fact]
    public async Task AllFailed_DefaultLabel_IsSearch()
    {
        var tiers = Tiers(new[] { "q1" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => throw new InvalidOperationException("inner"),
            SearchStopPolicy.AccumulateAll));

        Assert.Equal(
            "All 1 search request(s) failed; surfacing the error instead of an empty result.",
            ex.Message);
    }

    [Fact]
    public void ThrowAllFailed_Helper_MatchesExecutorContract()
    {
        var inner = new InvalidOperationException("inner");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SearchPlanExecutor.ThrowAllFailed(3, 0, inner, "Qobuz"));
        Assert.Equal(
            "All 3 Qobuz request(s) failed; surfacing the error instead of an empty result.",
            ex.Message);
        Assert.Same(inner, ex.InnerException);

        // Does NOT throw unless attempted>0 && succeeded==0 && lastError!=null.
        Assert.Null(Record.Exception(() => SearchPlanExecutor.ThrowAllFailed(0, 0, inner, "Qobuz")));
        Assert.Null(Record.Exception(() => SearchPlanExecutor.ThrowAllFailed(3, 1, inner, "Qobuz")));
        Assert.Null(Record.Exception(() => SearchPlanExecutor.ThrowAllFailed(3, 0, null, "Qobuz")));
    }

    // ===== Cancellation contract (the fix for the tidal mid-flight OCE swallow) =====

    [Fact]
    public async Task CancellationMidFlight_PropagatesOCE_NotRecordedAsFailure()
    {
        using var cts = new CancellationTokenSource();
        var observed = new List<string>();
        var called = new List<string>();
        var tiers = Tiers(new[] { "q1", "q2" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(WithAlbums("never"));
            },
            SearchStopPolicy.AccumulateAll,
            onError: (q, ex) => observed.Add(q),
            cancellationToken: cts.Token));

        // Only the first variant ran; the OCE propagated (not re-wrapped as InvalidOperationException)
        // and was NOT recorded as a failed variant / sent to onError.
        Assert.Equal(new[] { "q1" }, called);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task CancellationBeforeIteration_PropagatesOCE_WithoutInvokingDelegate()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var called = new List<string>();
        var tiers = Tiers(new[] { "q1" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(WithAlbums("never"));
            },
            SearchStopPolicy.AccumulateAll,
            cancellationToken: cts.Token));

        Assert.Empty(called);
    }

    [Fact]
    public async Task TaskCanceled_WhenTokenNotRequested_IsTreatedAsRecoverableFailure()
    {
        // An inner HTTP timeout surfaces TaskCanceledException while the caller's token is NOT cancelled
        // (the three None-passing plugins). This must be a normal recoverable failure, not a propagated
        // cancellation -> the second variant still runs and can recover.
        var tiers = Tiers(new[] { "timeout", "ok" });
        var observed = new List<string>();

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            tiers,
            (q, ct) => q == "timeout"
                ? throw new TaskCanceledException("inner http timeout")
                : Task.FromResult(WithAlbums("recovered")),
            SearchStopPolicy.AccumulateAll,
            onError: (q, ex) => observed.Add(q),
            cancellationToken: CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("recovered", results[0].Id);
        Assert.Equal(new[] { "timeout" }, observed);
    }

    // ===== SearchPlan convenience overload =====

    [Fact]
    public async Task SearchPlanOverload_ForwardsTiers()
    {
        var plan = SearchQuerySanitizer.BuildPlan("Daft Punk", "Discovery");
        var called = new List<string>();

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            plan,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(WithAlbums(q));
            },
            SearchStopPolicy.AccumulateAll);

        Assert.NotEmpty(called);
        // First query issued is the combined tier's first variant.
        Assert.Equal(plan.Tiers[0][0], called[0]);
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task EmptyPlan_IssuesNothing_ReturnsEmpty()
    {
        var called = new List<string>();

        var results = await SearchPlanExecutor.ExecuteAsync<Album>(
            SearchPlan.Empty,
            (q, ct) =>
            {
                called.Add(q);
                return Task.FromResult(WithAlbums(q));
            },
            SearchStopPolicy.StopAfterFirstTierWithResults);

        Assert.Empty(called);
        Assert.Empty(results);
    }
}
