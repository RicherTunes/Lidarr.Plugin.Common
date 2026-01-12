namespace Lidarr.Plugin.Abstractions.Results;

/// <summary>
/// Neutral, cross-plugin failure taxonomy for streaming plugins.
/// Use this to classify failures in a stable way without relying on exception-message parsing.
/// </summary>
public enum StreamingFailureReason
{
    None = 0,

    StreamingConfigMissing,
    StreamingConfigInvalid,

    StreamingAuthMissing,
    StreamingAuthExpired,
    StreamingAuthInvalid,

    StreamingRateLimited,
    StreamingServiceUnavailable,
    StreamingApiTimeout,

    StreamingCatalogEmpty,
    StreamingTrackUnavailable,
    StreamingQualityUnavailable,

    StreamingDownloadPayloadInvalid,
    StreamingDownloadTimeout,

    StreamingMetadataWriteFailed
}

