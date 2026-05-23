using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Thrown when a downloaded file's actual byte count does not match the expected total
/// declared by the server (via Content-Length or Content-Range).
///
/// When this exception is raised, the partial/final file has already been deleted so
/// the caller can retry the download from scratch without stale data.
/// </summary>
public sealed class DownloadIntegrityException : Exception
{
    /// <summary>Number of bytes actually written to disk.</summary>
    public long ActualBytes { get; }

    /// <summary>Number of bytes the server advertised (Content-Length or Content-Range total).</summary>
    public long ExpectedBytes { get; }

    /// <summary>
    /// Initialises a new <see cref="DownloadIntegrityException"/>.
    /// </summary>
    /// <param name="actualBytes">Bytes written.</param>
    /// <param name="expectedBytes">Bytes expected.</param>
    /// <param name="filePath">The destination file path (used in the message; not stored).</param>
    /// <param name="inner">Optional inner exception.</param>
    public DownloadIntegrityException(long actualBytes, long expectedBytes, string? filePath = null, Exception? inner = null)
        : base(BuildMessage(actualBytes, expectedBytes, filePath), inner)
    {
        ActualBytes = actualBytes;
        ExpectedBytes = expectedBytes;
    }

    private static string BuildMessage(long actual, long expected, string? filePath)
    {
        var fileHint = string.IsNullOrWhiteSpace(filePath) ? string.Empty : $" ({System.IO.Path.GetFileName(filePath)})";
        return $"Download integrity check failed{fileHint}: received {actual} bytes but expected {expected} bytes. " +
               "The partial file has been deleted; retry the download.";
    }
}
