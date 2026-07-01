using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Embeds cover artwork directly into a downloaded audio file (FLAC PICTURE / ID3 APIC).
    /// <para>
    /// Embedding — rather than writing a sidecar image — is deliberate: Lidarr's default
    /// <c>importExtraFiles=false</c> drops non-audio sidecars at import, so a folder/cover image
    /// produced next to the track never reaches the library. Cover art carried inside the audio
    /// file's tag survives the import move unconditionally.
    /// </para>
    /// </summary>
    public interface IAudioArtworkEmbedder
    {
        /// <summary>
        /// Embeds <paramref name="imageBytes"/> as the front-cover picture of the file at
        /// <paramref name="filePath"/>. Best-effort: a failure (invalid file, unreadable image)
        /// must never throw or fail the download — it is logged and swallowed. A null/empty image
        /// is a no-op.
        /// </summary>
        /// <param name="filePath">Path to the media file to tag.</param>
        /// <param name="imageBytes">Raw cover image bytes (e.g. JPEG/PNG).</param>
        /// <param name="mimeType">Image MIME type (defaults to <c>image/jpeg</c> when null/blank).</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        Task EmbedAsync(string filePath, byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default);
    }
}
