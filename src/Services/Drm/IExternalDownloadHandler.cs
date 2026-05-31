using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// Extension seam for integrating an <b>out-of-tree</b> downloader/DRM implementation
    /// shared across plugins that pull from DRM-protected streaming services
    /// (e.g. Apple Music, Amazon Music).
    ///
    /// <para>
    /// <b>DRM-out-of-Common stance:</b> Lidarr.Plugin.Common is public and shippable.
    /// It deliberately ships <b>only this seam</b> — the interface, the <see cref="DrmTrack"/>
    /// value model, the <see cref="ExternalDownloadProgress"/> record, and the reflection
    /// <see cref="ExternalDownloadHandlerLoader"/>. Common ships <b>NO handler implementation
    /// (not even a mock)</b>: no key derivation, no CDM, no license-challenge, and no
    /// decrypt/mux logic ever enters this library. The concrete decryptor is supplied
    /// out-of-tree by the user and loaded at runtime (see <see cref="ExternalDownloadHandlerLoader"/>).
    /// Implementations are solely responsible for any legal compliance.
    /// </para>
    ///
    /// <para>
    /// The seam is intentionally narrow: given a <see cref="DrmTrack"/> and an output file
    /// path, perform the end-to-end fetch/decrypt/mux and return <c>true</c> on success.
    /// </para>
    /// </summary>
    public interface IExternalDownloadHandler
    {
        /// <summary>
        /// Fetches, decrypts and muxes <paramref name="track"/> to <paramref name="outputPath"/>.
        /// Returns <c>true</c> on success. The implementation lives out-of-tree (see the type
        /// remarks); Common never provides one.
        /// </summary>
        /// <param name="track">DRM/playback metadata for the track to download.</param>
        /// <param name="outputPath">Absolute destination file path for the finished, decrypted file.</param>
        /// <param name="progress">Optional progress sink reporting percent/message updates.</param>
        /// <param name="ct">Cancellation token.</param>
        Task<bool> DownloadAsync(DrmTrack track, string outputPath, IProgress<ExternalDownloadProgress>? progress = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Progress update emitted by an <see cref="IExternalDownloadHandler"/> during a download.
    /// Both fields are optional so handlers can report a percentage, a status message, or both.
    /// </summary>
    /// <param name="Percent">Completion fraction in the range 0..100, or <c>null</c> when unknown.</param>
    /// <param name="Message">Human-readable status message, or <c>null</c>.</param>
    public sealed record ExternalDownloadProgress(double? Percent, string? Message);
}
