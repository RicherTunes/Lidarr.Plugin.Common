using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Models;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Applies audio metadata (tags, artwork, etc.) to downloaded files.
    /// </summary>
    public interface IAudioMetadataApplier
    {
        /// <summary>
        /// Applies metadata to the specified file.
        /// </summary>
        /// <param name="filePath">Path to the media file.</param>
        /// <param name="metadata">Metadata describing the track/album.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default);
    }
}
