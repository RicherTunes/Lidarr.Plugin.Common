# Shared Observability Proposal (Plugins)

## Goals
- Consistent, lightweight event set across plugins
- No new heavy deps; integrate with ILogger + Activity
- Enable correlation (Activity.Current) and basic latency/error telemetry

## Minimal Event Set
- ApiCallStarted(service, endpoint, correlationId)
- ApiCallCompleted(service, endpoint, statusCode, success, durationMs)
- DownloadChunkCompleted(trackId, bytes, durationMs)

## Shape
- EventId constants under Lidarr.Plugin.Common.Observability.EventIds
- Logger extensions LogApiCallStarted/Completed
- Optional Activity tags: service, endpoint, status_code

## Next Steps
- Agree on names and payloads
- Implement minimal helper in src/Observability/ with ILogger extensions
- Add sample usage in docs
