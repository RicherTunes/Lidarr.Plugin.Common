using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Thrown when the total number of items collected across all pages of a paged
/// API response does not match the <c>totalCount</c> declared by the server.
/// </summary>
/// <remarks>
/// Silent truncation — receiving fewer items than declared — is the most common
/// form of this defect, but over-fetching (e.g., duplicate pages) is equally
/// detected.  Callers should surface this as a data-integrity warning rather
/// than retrying blindly, because the mismatch often indicates a server-side
/// bug or a pagination logic error in the plugin.
/// </remarks>
public sealed class PagedResponseIntegrityException : Exception
{
    /// <summary>
    /// Total number of items actually received across all fetched pages.
    /// </summary>
    public int ReceivedItemCount { get; }

    /// <summary>
    /// Total count declared by the server in the paged response envelope.
    /// </summary>
    public int DeclaredTotalCount { get; }

    /// <summary>
    /// Caller-supplied name that identifies the operation or resource type
    /// (e.g., <c>"apple-music-albums"</c>).  Included in the exception message
    /// to aid diagnosis.
    /// </summary>
    public string ContextName { get; }

    /// <summary>
    /// Initialises a new <see cref="PagedResponseIntegrityException"/>.
    /// </summary>
    public PagedResponseIntegrityException(int receivedItemCount, int declaredTotalCount, string contextName)
        : base(BuildMessage(receivedItemCount, declaredTotalCount, contextName))
    {
        ReceivedItemCount = receivedItemCount;
        DeclaredTotalCount = declaredTotalCount;
        ContextName = contextName;
    }

    private static string BuildMessage(int received, int declared, string context) =>
        $"Paged response integrity check failed for '{context}': " +
        $"received {received} items but server declared {declared}. " +
        "Check for silent truncation, duplicate pages, or a server-side pagination bug.";
}
