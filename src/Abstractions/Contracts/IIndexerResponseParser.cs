using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Parses HTTP responses into domain models.
    /// Bridge plugins implement this to convert service-specific JSON into common models.
    /// </summary>
    /// <typeparam name="T">The response model type</typeparam>
    public interface IIndexerResponseParser<T> where T : class
    {
        /// <summary>
        /// Parses a raw response string into the model type.
        /// </summary>
        /// <param name="responseContent">Raw HTTP response content</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Parsed model instance</returns>
        Task<T> ParseAsync(string responseContent, CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses a response into a list of albums.
        /// </summary>
        /// <param name="responseContent">Raw HTTP response content</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of streaming albums</returns>
        Task<IReadOnlyList<StreamingAlbum>> ParseAlbumsAsync(string responseContent, CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses a response into a list of tracks.
        /// </summary>
        /// <param name="responseContent">Raw HTTP response content</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of streaming tracks</returns>
        Task<IReadOnlyList<StreamingTrack>> ParseTracksAsync(string responseContent, CancellationToken cancellationToken = default);
    }
}
