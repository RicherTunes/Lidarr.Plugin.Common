Shared Download Orchestrator (Early Adapter)

Overview

- SimpleDownloadOrchestrator provides a minimal, robust download flow for streaming plugins:
  - Partial file writes with Range resume when supported
  - Atomic move from .partial to final path
  - Pluggable delegates for album/track lookups and stream URL resolution
  - Works with any HttpClient configured by your plugin (incl. OAuthDelegatingHandler)

Quick Start

- Construct with your service delegates:

  - getAlbumAsync: albumId -> StreamingAlbum (basic metadata; TrackCount is used for progress only)
  - getAlbumTrackIdsAsync: albumId -> IReadOnlyList<string> of track IDs
  - getTrackAsync: trackId -> StreamingTrack (for filename and metadata)
  - getStreamAsync: (trackId, StreamingQuality?) -> (Url, Extension) for the final container

Example (Tidal-like)

  var orchestrator = new SimpleDownloadOrchestrator(
      serviceName: "Tidal",
      httpClient: oauthHttpClient,
      getAlbumAsync: id => tidalApi.GetAlbumAsync(id),
      getTrackAsync: id => tidalApi.GetTrackAsync(id),
      getAlbumTrackIdsAsync: id => tidalApi.GetAlbumTrackIdsAsync(id),
      getStreamAsync: async (id, q) =>
      {
          var info = await tidalCore.GetStreamInfoAsync(id, tidalMapper.FromStreamingQuality(q));
          return (info.ChunkUrls.FirstOrDefault() ?? string.Empty, info.FileExtension.TrimStart('.'));
      });

  // Album download
  var result = await orchestrator.DownloadAlbumAsync(albumId, outputDir, preferredQuality);

  // Single track download
  var trackResult = await orchestrator.DownloadTrackAsync(trackId, fullOutputPath, preferredQuality);

Resume + Atomicity

- The orchestrator writes to <output>.partial and persists a small JSON checkpoint (<output>.partial.resume.json)
  with downloaded byte count. If the server returns 206 Partial Content, it appends; a 200 OK causes a fresh
  restart (old .partial is deleted). After success, it deletes the .partial and .resume.json and atomically moves
  to the final file.

NzbDrone HTTP Resilience

- Use GenericResilienceExecutor to apply the same resilience behavior to NzbDrone.Common.Http. See:
  examples/NzbDroneResilienceAdapter.cs

Notes

- This is an early adapter to unblock consolidation. A full orchestrator will add:
  - Per-track progress callbacks (speed, ETA)
  - Resume checkpoints with integrity markers (ETag/Last-Modified) where available
  - Optional signature checks and metadata/tagging hooks
  - Queue management and cancellation

