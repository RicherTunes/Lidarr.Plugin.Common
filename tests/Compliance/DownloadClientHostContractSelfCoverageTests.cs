using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Self-coverage for <see cref="DownloadClientHostContractTestBase"/>: proves the shared guard PASSES on
/// a correct projection and CATCHES each real-world violation (the tidal/apple copy-only + amazon/apple
/// Cancelled-to-Queued + id=0 + duplicate-id bugs) — before any plugin adopts the axis. The fakes are
/// private nested classes so xUnit does not discover them as tests in their own right.
/// </summary>
public class DownloadClientHostContractSelfCoverageTests
{
    private sealed class GoodProjection : DownloadClientHostContractTestBase
    {
        protected override HostDownloadItemView Completed() =>
            new("dl-1", ClientId: 7, Status: "Completed", CanMoveFiles: true, CanBeRemoved: true);

        protected override HostDownloadItemView Failed() =>
            new("dl-2", ClientId: 7, Status: "Failed", CanMoveFiles: false, CanBeRemoved: true);

        protected override HostDownloadItemView? Cancelled() =>
            new("dl-3", ClientId: 7, Status: "Warning", CanMoveFiles: false, CanBeRemoved: true);

        protected override IReadOnlyList<HostDownloadItemView> DuplicateDownloadId(string downloadId) =>
            new[] { new HostDownloadItemView(downloadId, 7, "Completed", true, true) };
    }

    // Reproduces every shipped violation at once.
    private sealed class BrokenProjection : DownloadClientHostContractTestBase
    {
        protected override HostDownloadItemView Completed() =>
            new("dl-1", ClientId: 0, Status: "Completed", CanMoveFiles: false, CanBeRemoved: false); // tidal/apple + id=0

        protected override HostDownloadItemView Failed() =>
            new("dl-2", ClientId: 0, Status: "Failed", CanMoveFiles: false, CanBeRemoved: false);

        protected override HostDownloadItemView? Cancelled() =>
            new("dl-3", ClientId: 0, Status: "Queued", CanMoveFiles: false, CanBeRemoved: false); // amazon/apple

        protected override IReadOnlyList<HostDownloadItemView> DuplicateDownloadId(string downloadId) =>
            new[]
            {
                new HostDownloadItemView(downloadId, 0, "Completed", false, false),
                new HostDownloadItemView(downloadId, 0, "Completed", false, false), // not deduped
            };
    }

    [Fact]
    public void Base_passes_on_a_correct_projection()
    {
        var good = new GoodProjection();
        good.CompletedDownload_CanMoveFiles_AndCanBeRemoved();
        good.AllItems_CarryClientDefinitionId_NotZero();
        good.CancelledDownload_IsNotReportedAsInProgress();
        good.GetItems_DedupesByDownloadId();
    }

    [Fact]
    public void Base_catches_copyOnly_completed()
        => Assert.ThrowsAny<Exception>(() => new BrokenProjection().CompletedDownload_CanMoveFiles_AndCanBeRemoved());

    [Fact]
    public void Base_catches_cancelled_reported_as_queued()
        => Assert.ThrowsAny<Exception>(() => new BrokenProjection().CancelledDownload_IsNotReportedAsInProgress());

    [Fact]
    public void Base_catches_zero_clientId()
        => Assert.ThrowsAny<Exception>(() => new BrokenProjection().AllItems_CarryClientDefinitionId_NotZero());

    [Fact]
    public void Base_catches_duplicate_downloadId()
        => Assert.ThrowsAny<Exception>(() => new BrokenProjection().GetItems_DedupesByDownloadId());
}
