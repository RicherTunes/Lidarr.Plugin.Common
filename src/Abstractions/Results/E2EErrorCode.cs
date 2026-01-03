namespace Lidarr.Plugin.Abstractions.Results
{
    /// <summary>
    /// Standardized E2E error codes for CI triage and diagnostics.
    /// These codes are emitted in run-manifest.json for machine-readable failure classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plugins should emit these codes in error metadata using <see cref="E2EErrorCodeExtensions.WithE2EErrorCode"/>
    /// to enable direct classification without regex inference.
    /// </para>
    /// <para>
    /// The E2E runner checks for explicit error codes before falling back to pattern matching.
    /// </para>
    /// </remarks>
    public enum E2EErrorCode
    {
        /// <summary>
        /// No specific error code (default for regex-inferred errors).
        /// </summary>
        None = 0,

        /// <summary>
        /// Required credentials are missing for the requested gate.
        /// Causes: Secrets/env vars not set, component not configured, masked values without overwrite.
        /// </summary>
        AuthMissing,

        /// <summary>
        /// Configuration exists but fails server-side validation.
        /// Causes: Wrong redirectUrl, invalid market, missing download path, field shape mismatch.
        /// </summary>
        ConfigInvalid,

        /// <summary>
        /// A Lidarr API call or polling loop timed out.
        /// Causes: Slow startup, host under load, network issues, gate timeout too low.
        /// </summary>
        ApiTimeout,

        /// <summary>
        /// Docker interaction required but not available.
        /// Causes: Docker not running, insufficient permissions, wrong container name.
        /// </summary>
        DockerUnavailable,

        /// <summary>
        /// AlbumSearch returned releases but none attributed to the target plugin.
        /// Causes: Indexer not used, parser regression, wrong indexer ID, cached results.
        /// </summary>
        NoReleasesAttributed,

        /// <summary>
        /// Grab triggered but expected queue item could not be correlated.
        /// Causes: Download client didn't enqueue, correlation mismatch, API change.
        /// </summary>
        QueueNotFound,

        /// <summary>
        /// Download completed but produced zero validated audio files.
        /// Causes: Output path wrong, partial files, stream failure, extension filter mismatch.
        /// </summary>
        ZeroAudioFiles,

        /// <summary>
        /// Audio file(s) exist but required metadata tags are missing.
        /// Causes: Metadata applier not wired, TagLib limitations, incomplete model mapping.
        /// </summary>
        MetadataMissing,

        /// <summary>
        /// ImportListSync completed with errors or post-sync state indicates failure.
        /// Causes: Provider unreachable, LLM endpoint down, import list misconfigured.
        /// </summary>
        ImportFailed,

        /// <summary>
        /// Multiple configured components match the plugin (ambiguous selection).
        /// Causes: Duplicate indexers/download clients with same implementation name.
        /// </summary>
        ComponentAmbiguous,

        /// <summary>
        /// Assembly or type loading failure.
        /// Causes: ALC bug, ABI mismatch, dependency drift, missing dependencies.
        /// </summary>
        LoadFailure,

        /// <summary>
        /// Rate limited by upstream provider.
        /// Causes: Too many requests, quota exceeded, temporary throttling.
        /// </summary>
        RateLimited,

        /// <summary>
        /// Provider service is unavailable.
        /// Causes: Upstream outage, maintenance window, network partition.
        /// </summary>
        ProviderUnavailable,

        /// <summary>
        /// Request was cancelled.
        /// Causes: User cancellation, timeout cancellation, graceful shutdown.
        /// </summary>
        Cancelled
    }
}
