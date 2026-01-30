# How-to: Use the Shared Download Orchestrator

`SimpleDownloadOrchestrator` gives plugins a resilient download loop with resume support and atomic writes. This guide shows how to wire it up without duplicating boilerplate.

## Capabilities

- Streams to `<file>.partial` then atomically moves to the final path.
- Resumes downloads when the server supports `Range` requests.
- Exposes delegates so you control how albums/tracks/URLs are resolved.
- Integrates with your existing `HttpClient` (including OAuth handlers).
- Supports optional post-processing (e.g., remux/extract) via `IAudioPostProcessor`.

## 1. Configure delegates

```csharp

var orchestrator = new SimpleDownloadOrchestrator(
    serviceName: "Tidal",
    httpClient: oauthHttpClient,
    getAlbumAsync: tidalApi.GetAlbumAsync,
    getTrackAsync: tidalApi.GetTrackAsync,
    getAlbumTrackIdsAsync: tidalApi.GetAlbumTrackIdsAsync,
    getStreamAsync: async (trackId, quality) =>
    {
        var info = await tidalCore.GetStreamInfoAsync(trackId, tidalMapper.FromStreamingQuality(quality));
        return (info.Url, info.Extension.TrimStart('.'));
    });

```

Delegate responsibilities:

- `getAlbumAsync(string)` → `StreamingAlbum`
- `getAlbumTrackIdsAsync(string)` → ordered track ids
- `getTrackAsync(string)` → `StreamingTrack`
- `getStreamAsync(string, StreamingQuality?)` → `(downloadUrl, extension)`

## 2. Download tracks or albums

```csharp

await orchestrator.DownloadAlbumAsync(albumId, outputDirectory, preferredQuality);
await orchestrator.DownloadTrackAsync(trackId, trackPath, preferredQuality);

```

The orchestrator reports progress via `DownloadProgress` delegates (subscribe if you need UI updates).

## 3. Resume behaviour

- Downloads write to `<target>.partial` and a checkpoint JSON file.
- A `206 Partial Content` response resumes from the last saved byte.
- A `200 OK` response triggers a fresh download (old partial file cleaned up automatically).
- On success the `.partial` and checkpoint files are deleted and the final file is moved atomically.

## 4. Optional audio post-processing (`IAudioPostProcessor`)

If your plugin needs to change the downloaded file after it’s written (for example: extracting FLAC from an M4A container), implement `IAudioPostProcessor` and pass it into the orchestrator.

Lifecycle contract:

- Runs after the `.partial` file has been atomically moved to the final path.
- Runs before tagging/metadata steps (so tagging sees the final format).
- May return the original `filePath` unchanged, or a new path (for example, `track.m4a` → `track.flac`).
- Must never produce mislabeled output on failure (e.g., a `.flac` file containing M4A bytes); on failure, keep the original file and return the original path.
- Should be resilient: handle missing tools (ffmpeg) by returning the original path and logging a warning.

## 5. Harden the HTTP pipeline

- Wrap your `HttpClient` in `HttpClientExtensions.ExecuteWithResilienceAsync`.
- Set `perRequestTimeout` on resilience calls when the service enforces strict SLAs; exceeding it throws `TimeoutException`.
- Keep `ContentDecodingSnifferHandler` in the handler chain so mislabelled gzip responses inflate cleanly (it clears `Content-Encoding`/`Content-Length` for you).
- Use `OAuthDelegatingHandler` or another `IStreamingTokenProvider`-backed handler if tokens expire.
- Optionally rate-limit with `IUniversalAdaptiveRateLimiter`.

## 6. Testing tips

- Mock delegates to simulate success + failure paths.
- Use the filesystem abstraction from Common to redirect downloads to a temporary directory during tests.
- Check partial/ resume files exist during a simulated crash and disappear after a resumed success.

## Related docs

- [Developer guide → Downloads](../dev-guide/DEVELOPER_GUIDE.md#downloads)
- [Create a plugin project](CREATE_PLUGIN.md)
- [Add logging](ADD_LOGGING.md)
