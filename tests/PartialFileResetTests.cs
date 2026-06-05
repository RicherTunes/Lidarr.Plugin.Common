using System.IO;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// LOOP-003: a resumable download must NOT append a fresh full body onto stale <c>.partial</c> bytes when the
    /// server ignored the resume <c>Range</c> (answered 200, not 206). The previous code deleted the stale partial
    /// best-effort (swallowing failures) and then opened <c>FileMode.Append</c> unconditionally — so a delete that
    /// failed left stale+fresh on disk (silent corruption; the byte-count check counts session bytes, not file
    /// size, so it passes). PartialFileReset chooses <c>Create</c> (truncate) on a non-206 so the write is
    /// fail-closed regardless of whether the delete succeeded.
    /// </summary>
    public sealed class PartialFileResetTests
    {
        [Fact]
        public void ResolveWriteMode_PartialContent_Appends()
        {
            // 206: the server honored the Range, so the existing .partial bytes are the prefix — append to them.
            Assert.Equal(FileMode.Append, PartialFileReset.ResolveWriteMode(serverHonoredRange: true));
        }

        [Fact]
        public void ResolveWriteMode_FullResponse_Truncates()
        {
            // 200 (or anything not 206): the body is the WHOLE file — truncate so stale partial bytes can't be
            // appended onto, even if the best-effort delete failed.
            Assert.Equal(FileMode.Create, PartialFileReset.ResolveWriteMode(serverHonoredRange: false));
        }
    }
}
