using System;
using System.IO;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Upstreams the qobuz cross-attempt cleanup race-guard into Common so every plugin using
    /// HostBridgeDownloadTrackerStore.Remove(deleteData:true) is protected. When the host re-grabs a
    /// failed album, the new attempt writes into the SAME output directory; the old item's removal must
    /// NOT delete that directory out from under the new in-flight attempt (which caused an infinite
    /// re-grab loop on Qobuz). Remove() now skips the directory delete when another active (Queued/
    /// Downloading) tracked item targets the same OutputPath.
    /// </summary>
    public sealed class HostBridgeDownloadTrackerCleanupRaceTests
    {
        private static string MakeTempDirWithFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hbtcr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "track.flac.partial"), "x");
            return dir;
        }

        [Fact]
        public void Remove_deleteData_skips_deletion_when_another_active_download_targets_same_path()
        {
            var dir = MakeTempDirWithFile();
            try
            {
                var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
                var oldAttempt = new HostBridgeDownloadItem { DownloadId = "old", Title = "T", Artist = "A", OutputPath = dir };
                oldAttempt.SetStatus(HostBridgeDownloadItemStatus.Failed);
                var newAttempt = new HostBridgeDownloadItem { DownloadId = "new", Title = "T", Artist = "A", OutputPath = dir };
                newAttempt.SetStatus(HostBridgeDownloadItemStatus.Downloading);
                store.AddOrReplace(oldAttempt);
                store.AddOrReplace(newAttempt);

                var removed = store.Remove("old", deleteData: true, out _);

                Assert.True(removed);
                Assert.True(Directory.Exists(dir),
                    "cleanup must be skipped: a new active download targets the same path (cross-attempt re-grab guard)");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }

        [Fact]
        public void Remove_deleteData_deletes_directory_when_no_other_active_download()
        {
            var dir = MakeTempDirWithFile();
            try
            {
                var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
                var item = new HostBridgeDownloadItem { DownloadId = "solo", Title = "T", Artist = "A", OutputPath = dir };
                item.SetStatus(HostBridgeDownloadItemStatus.Failed);
                store.AddOrReplace(item);

                var removed = store.Remove("solo", deleteData: true, out _);

                Assert.True(removed);
                Assert.False(Directory.Exists(dir),
                    "with no competing active download, the failed attempt's directory is cleaned up (legacy behavior preserved)");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }

        [Fact]
        public void Remove_deleteData_deletes_when_other_same_path_download_is_terminal()
        {
            var dir = MakeTempDirWithFile();
            try
            {
                var store = new HostBridgeDownloadTrackerStore<HostBridgeDownloadItem>();
                var a = new HostBridgeDownloadItem { DownloadId = "a", Title = "T", Artist = "A", OutputPath = dir };
                a.SetStatus(HostBridgeDownloadItemStatus.Failed);
                var b = new HostBridgeDownloadItem { DownloadId = "b", Title = "T", Artist = "A", OutputPath = dir };
                b.SetStatus(HostBridgeDownloadItemStatus.Completed); // terminal, not active — must not block cleanup
                store.AddOrReplace(a);
                store.AddOrReplace(b);

                store.Remove("a", deleteData: true, out _);

                Assert.False(Directory.Exists(dir),
                    "a terminal (completed/failed/cancelled) item at the same path must not block cleanup");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }
    }
}
