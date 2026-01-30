using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Common.Interfaces;

public interface IAudioPostProcessor
{
    /// <summary>
    /// Optional post-processing step that runs after a track is downloaded and moved to its final
    /// path, but before metadata tagging is applied. Implementations may replace the file and return
    /// a new path (e.g., converting containers or changing extensions).
    /// </summary>
    /// <remarks>
    /// Implementations should be resilient: return <paramref name="filePath"/> if no processing is required
    /// or if processing cannot be performed safely.
    /// </remarks>
    Task<string> PostProcessAsync(string filePath, StreamingTrack track, StreamingQuality? quality, CancellationToken cancellationToken);
}
