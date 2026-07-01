using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TagLib;

namespace Lidarr.Plugin.Common.Services.Metadata
{
    /// <summary>
    /// TagLib-backed <see cref="IAudioArtworkEmbedder"/>. Writes the supplied image as the single
    /// front-cover picture on the file (FLAC PICTURE block / ID3v2 APIC / MP4 covr). Shared across
    /// plugins so every streaming download arrives with embedded art that survives Lidarr import.
    /// </summary>
    public sealed class TagLibAudioArtworkEmbedder : IAudioArtworkEmbedder
    {
        private const string DefaultMimeType = "image/jpeg";

        private readonly ILogger _logger;

        public TagLibAudioArtworkEmbedder()
            : this(NullLogger.Instance)
        {
        }

        public TagLibAudioArtworkEmbedder(ILogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public Task EmbedAsync(string filePath, byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || imageBytes == null || imageBytes.Length == 0)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                try
                {
                    using var file = TagLib.File.Create(filePath);

                    var picture = new TagLib.Picture(new TagLib.ByteVector(imageBytes))
                    {
                        Type = PictureType.FrontCover,
                        MimeType = string.IsNullOrWhiteSpace(mimeType) ? DefaultMimeType : mimeType,
                        Description = "Cover",
                    };

                    file.Tag.Pictures = new IPicture[] { picture };
                    file.Save();
                }
                catch (Exception ex)
                {
                    // Graceful degradation: artwork embedding failures must never fail downloads.
                    _logger.LogWarning(
                        "[TagLibAudioArtworkEmbedder] Failed to embed artwork into '{FileName}': {ErrorType}",
                        Path.GetFileName(filePath),
                        ex.GetType().Name);
                }
            }, cancellationToken);
        }
    }
}
