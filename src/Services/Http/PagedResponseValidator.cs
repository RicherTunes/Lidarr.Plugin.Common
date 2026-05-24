using Lidarr.Plugin.Common.Errors;

namespace Lidarr.Plugin.Common.Services.Http;

/// <summary>
/// Validates that the total number of items collected across all fetched pages
/// of a paged API response matches the server-declared total count.
/// </summary>
/// <remarks>
/// Many plugin paged-API consumers iterate pages and accumulate items without
/// ever verifying that the accumulated count equals the <c>totalCount</c> field
/// in the response envelope.  This allows silent truncation — where the last
/// page is missing or a server bug returns fewer results — to go undetected.
/// <para>
/// Call <see cref="Validate"/> after exhausting all pages and passing the
/// summed item count alongside the <c>totalCount</c> extracted from the first
/// (or last) page envelope.  Pass <see langword="null"/> for
/// <c>declaredTotalCount: null</c> when the API does not declare a total
/// (validation is skipped in that case).
/// </para>
/// </remarks>
public static class PagedResponseValidator
{
    /// <summary>
    /// Asserts that <paramref name="receivedItemCount"/> equals
    /// <paramref name="declaredTotalCount"/>.
    /// </summary>
    /// <param name="receivedItemCount">
    /// Sum of <c>page.Items.Count</c> across all fetched pages.
    /// </param>
    /// <param name="declaredTotalCount">
    /// The <c>totalCount</c> (or equivalent) field from the paged response
    /// envelope, or <see langword="null"/> if the API does not declare one.
    /// When <see langword="null"/>, this method returns without throwing.
    /// </param>
    /// <param name="contextName">
    /// Short descriptive name of the operation or resource type, used in the
    /// exception message (e.g., <c>"apple-music-albums"</c>).
    /// </param>
    /// <exception cref="PagedResponseIntegrityException">
    /// Thrown when <paramref name="declaredTotalCount"/> is not
    /// <see langword="null"/> and does not equal <paramref name="receivedItemCount"/>.
    /// </exception>
    public static void Validate(int receivedItemCount, int? declaredTotalCount, string contextName)
    {
        if (declaredTotalCount is null)
            return;

        if (receivedItemCount != declaredTotalCount.Value)
            throw new PagedResponseIntegrityException(receivedItemCount, declaredTotalCount.Value, contextName);
    }
}
