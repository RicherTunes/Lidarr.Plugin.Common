using System.IO;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// LOOP-003: chooses the <see cref="FileMode"/> for writing a (possibly resumed) download body to its
    /// <c>.partial</c> file, so a fresh full body can never be appended onto stale bytes.
    ///
    /// <para>When the server honored the resume <c>Range</c> (HTTP 206 Partial Content) the existing
    /// <c>.partial</c> bytes are the verified prefix, so the new bytes are appended. For any other response
    /// (200 OK — the server ignored the Range and is sending the WHOLE file) the write must <b>truncate</b>:
    /// <see cref="FileMode.Create"/> resets the file as it opens, so a stale <c>.partial</c> that a best-effort
    /// delete failed to remove cannot corrupt the download by being appended onto. This is fail-closed — it does
    /// not rely on the delete succeeding.</para>
    /// </summary>
    public static class PartialFileReset
    {
        /// <summary>
        /// <see cref="FileMode.Append"/> when <paramref name="serverHonoredRange"/> (HTTP 206), otherwise
        /// <see cref="FileMode.Create"/> (truncate the whole file).
        /// </summary>
        public static FileMode ResolveWriteMode(bool serverHonoredRange)
            => serverHonoredRange ? FileMode.Append : FileMode.Create;
    }
}
