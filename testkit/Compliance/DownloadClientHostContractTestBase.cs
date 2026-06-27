using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Host-type-free projection of a single host <c>DownloadClientItem</c> as a plugin's REAL GetItems
/// path produced it. The plugin's adapter reads the host item (CanMoveFiles, CanBeRemoved,
/// DownloadClientInfo.Id, Status) and maps it onto this record — so the shared contract assertions
/// live in Common (no host-assembly dependency) while the host types stay inside the plugin.
/// </summary>
public readonly record struct HostDownloadItemView(
    string DownloadId,
    int ClientId,
    string Status,
    bool CanMoveFiles,
    bool CanBeRemoved);

/// <summary>
/// Shared host-contract suite for every plugin's download client. It pins the boundary invariants that
/// only ever surfaced as live import bugs (unit tests covering the converter in isolation missed them):
/// <list type="bullet">
/// <item><b>CanMoveFiles / CanBeRemoved on a completed download</b> — host defaults are <c>false</c>;
/// leaving them unset makes Lidarr import copy-only and never emit a remove event (downloads pile up).
/// tidal/apple shipped without these.</item>
/// <item><b>DownloadClientInfo.Id == the client's Definition.Id (never 0)</b> — a zero id makes
/// <c>DownloadClientProvider.Get(id)</c> throw and wedges every completed download at import.</item>
/// <item><b>Cancelled is not reported as in-progress</b> — mapping Cancelled to Queued/Downloading
/// loops forever; it must surface as a terminal state (Warning or Failed). amazon/apple mapped it to
/// Queued.</item>
/// <item><b>GetItems dedups by DownloadId</b> — the same id from two sources makes Lidarr create two
/// queue entries and wedge at importPending.</item>
/// </list>
/// A plugin adopts the axis by mapping its real GetItems output onto <see cref="HostDownloadItemView"/>.
/// </summary>
public abstract class DownloadClientHostContractTestBase
{
    /// <summary>Project a COMPLETED download (with a non-empty output path) through the real GetItems path.</summary>
    protected abstract HostDownloadItemView Completed();

    /// <summary>Project a FAILED download through the real GetItems path.</summary>
    protected abstract HostDownloadItemView Failed();

    /// <summary>
    /// Project a CANCELLED download, or return <c>null</c> when the plugin has no Cancelled state in its
    /// status model (e.g. qobuz). When non-null it must NOT surface as Queued/Downloading.
    /// </summary>
    protected abstract HostDownloadItemView? Cancelled();

    /// <summary>
    /// Project GetItems when the same <paramref name="downloadId"/> is present twice (e.g. a tracker
    /// snapshot merged with an active-queue list). The result must contain it exactly once.
    /// </summary>
    protected abstract IReadOnlyList<HostDownloadItemView> DuplicateDownloadId(string downloadId);

    [Fact]
    public void CompletedDownload_CanMoveFiles_AndCanBeRemoved()
    {
        var item = Completed();
        Assert.True(item.CanMoveFiles,
            "a completed download must set CanMoveFiles or Lidarr imports copy-only and never cleans up the source");
        Assert.True(item.CanBeRemoved,
            "a completed download must set CanBeRemoved or Lidarr never emits the post-import remove event");
    }

    [Fact]
    public void AllItems_CarryClientDefinitionId_NotZero()
    {
        Assert.NotEqual(0, Completed().ClientId);
        Assert.NotEqual(0, Failed().ClientId);
    }

    [Fact]
    public void CancelledDownload_IsNotReportedAsInProgress()
    {
        var cancelled = Cancelled();
        if (cancelled is null)
        {
            return; // plugin has no Cancelled state — nothing to assert
        }

        Assert.False(
            cancelled.Value.Status is "Queued" or "Downloading",
            $"Cancelled must surface as a terminal status (Warning/Failed), not '{cancelled.Value.Status}' " +
            "— Queued/Downloading never resolves and the download wedges in the queue forever");
    }

    [Fact]
    public void GetItems_DedupesByDownloadId()
    {
        const string dup = "dup-download-id";
        var items = DuplicateDownloadId(dup);
        Assert.Equal(1, items.Count(i => i.DownloadId == dup));
    }
}
