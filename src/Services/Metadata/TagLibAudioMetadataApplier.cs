using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Models;
using TagLib;

namespace Lidarr.Plugin.Common.Services.Metadata
{
    internal sealed class TagLibAudioMetadataApplier : IAudioMetadataApplier
    {
        public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || metadata == null)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                using var file = TagLib.File.Create(filePath);

                if (!string.IsNullOrEmpty(metadata.Title))
                {
                    file.Tag.Title = metadata.Title;
                }

                if (!string.IsNullOrEmpty(metadata.Artist?.Name))
                {
                    file.Tag.Performers = new[] { metadata.Artist.Name };
                }

                if (!string.IsNullOrEmpty(metadata.Album?.Title))
                {
                    file.Tag.Album = metadata.Album.Title;
                }

                if (metadata.TrackNumber.HasValue && metadata.TrackNumber.Value > 0)
                {
                    file.Tag.Track = (uint)metadata.TrackNumber.Value;
                }

                if (metadata.Album?.ReleaseDate.HasValue == true)
                {
                    file.Tag.Year = (uint)metadata.Album.ReleaseDate.Value.Year;
                }

                if (metadata.Album?.Genres?.Any() == true)
                {
                    file.Tag.Genres = new[] { metadata.Album.Genres.First() };
                }

                file.Save();
            }, cancellationToken);
        }
    }
}
