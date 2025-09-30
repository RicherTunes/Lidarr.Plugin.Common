using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Provides an assembled audio stream for a specific track and quality.
    /// Implementations can gather bytes from chunked sources (e.g., DASH/HLS) or any custom transport.
    /// </summary>
    public interface IAudioStreamProvider
    {
        Task<AudioStreamResult> GetStreamAsync(string trackId, StreamingQuality? quality = null, CancellationToken cancellationToken = default);
    }

    public class AudioStreamResult
    {
        public Stream Stream { get; set; }
        public long? TotalBytes { get; set; }
        public string SuggestedExtension { get; set; }
    }
}
