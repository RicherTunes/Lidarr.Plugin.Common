using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Static assertions that exercise the LIVE GitHub releases of a plugin repo through
/// Lidarr's <c>PluginService.GetRemotePlugin</c> filter — the exact code path that
/// users hit when clicking "Install" on a GitHub URL in Lidarr.
///
/// <para>What this catches that the local <see cref="PluginPackagingContract"/> can't:</para>
/// the local PackagingContract covers the BUILD artifact; this covers the PUBLISHED
/// artifact. Catches the case where the publish step diverges from the build —
/// someone hand-uploads a stale zip, renames one in a release rebuild, or
/// release.yml's <c>files:</c> list points at the wrong artifact.
///
/// <para>Usage:</para>
/// <code>
/// public class BrainarrPublishedReleaseTests
/// {
///     [SkippableFact, Trait("Category", "ReleaseE2E")]
///     public Task LatestRelease_PassesLidarrFilter() =>
///         PublishedReleaseInstallabilityContract.AssertLatestReleasePassesLidarrInstallFilterAsync(
///             owner: "RicherTunes", repo: "Brainarr");
///
///     [SkippableFact, Trait("Category", "ReleaseE2E")]
///     public Task LatestRelease_ContentsMatchPolicy() =>
///         PublishedReleaseInstallabilityContract.AssertLatestReleaseZipMatchesPolicyAsync(
///             owner: "RicherTunes", repo: "Brainarr",
///             policy: PluginPackagingContract.MergedDllPolicy("Lidarr.Plugin.Brainarr",
///                 extraRequired: new[] { "manifest.json" }));
/// }
/// </code>
///
/// <para>Each assertion <c>Skip.If</c>s when GitHub is unreachable or rate-limited
/// — the tests are opt-in via <c>[Trait("Category", "ReleaseE2E")]</c> and tolerant
/// of network conditions. Honors <c>GITHUB_TOKEN</c> env var for authenticated
/// requests (higher rate limits in CI).</para>
/// </summary>
public static class PublishedReleaseInstallabilityContract
{
    /// <summary>
    /// Source-of-truth Lidarr install filter mirrored from
    /// <c>src/NzbDrone.Core/Plugins/PluginService.cs::GetRemotePlugin</c> on the plugins branch:
    /// <list type="number">
    ///   <item><c>!Draft</c></item>
    ///   <item><c>target_commitish</c> ∈ <c>{main, master}</c> (case-insensitive)</item>
    ///   <item>at least one asset's name contains the framework token (e.g. <c>net8.0.zip</c>)</item>
    /// </list>
    /// Asserts at least one release in <c>{owner}/{repo}</c> satisfies all three —
    /// if none does, the UI Install button silently no-ops (the failure mode behind
    /// the May 2026 install bug).
    /// </summary>
    public static async Task AssertLatestReleasePassesLidarrInstallFilterAsync(
        string owner, string repo, string framework = "net8.0", HttpClient? http = null)
    {
        var ownedClient = http is null;
        http ??= CreateClient(repo);
        try
        {
            var releases = await TryGetReleasesAsync(http, owner, repo).ConfigureAwait(false);
            Skip.If(releases is null, $"GitHub releases for {owner}/{repo} unavailable — network down or rate-limited.");

            var installable = releases!.Value.EnumerateArray()
                .Where(r => !r.GetProperty("draft").GetBoolean())
                .Where(r => IsDefaultTree(r.GetProperty("target_commitish").GetString()))
                .Where(r => r.GetProperty("assets").EnumerateArray()
                    .Any(a => a.GetProperty("name").GetString()?
                        .Contains($"{framework}.zip", StringComparison.OrdinalIgnoreCase) == true))
                .ToList();

            Assert.True(installable.Count > 0,
                $"No release in {owner}/{repo} passes Lidarr's PluginService filter. " +
                $"At least one non-draft release on main/master with an asset whose name contains " +
                $"'{framework}.zip' is required. UI Install on https://github.com/{owner}/{repo} " +
                $"would silently fail.");
        }
        finally
        {
            if (ownedClient) http.Dispose();
        }
    }

    /// <summary>
    /// Downloads the most recent installable release's matching asset (per the filter
    /// above) and delegates to <see cref="PluginPackagingContract.AssertZipMatchesPolicy"/>.
    /// </summary>
    public static async Task AssertLatestReleaseZipMatchesPolicyAsync(
        string owner, string repo, PluginPackagePolicy policy,
        string framework = "net8.0", HttpClient? http = null)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        var ownedClient = http is null;
        http ??= CreateClient(repo);
        try
        {
            var releases = await TryGetReleasesAsync(http, owner, repo).ConfigureAwait(false);
            Skip.If(releases is null, $"GitHub releases for {owner}/{repo} unavailable — network down or rate-limited.");

            var topRelease = releases!.Value.EnumerateArray()
                .Where(r => !r.GetProperty("draft").GetBoolean())
                .Where(r => IsDefaultTree(r.GetProperty("target_commitish").GetString()))
                .FirstOrDefault(r => r.GetProperty("assets").EnumerateArray()
                    .Any(a => a.GetProperty("name").GetString()?
                        .Contains($"{framework}.zip", StringComparison.OrdinalIgnoreCase) == true));

            Skip.If(topRelease.ValueKind == JsonValueKind.Undefined,
                $"No installable release found in {owner}/{repo} (see the install-filter test).");

            var asset = topRelease.GetProperty("assets").EnumerateArray()
                .First(a => a.GetProperty("name").GetString()?
                    .Contains($"{framework}.zip", StringComparison.OrdinalIgnoreCase) == true);

            var downloadUrl = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidOperationException("Asset has no browser_download_url.");

            // Download to temp file (instead of holding the whole zip in memory) so
            // PluginPackagingContract.AssertZipMatchesPolicy can open it with ZipFile.OpenRead.
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"lpcommon-relE2E-{Guid.NewGuid():N}-{asset.GetProperty("name").GetString()}");
            try
            {
                await using (var src = await http.GetStreamAsync(downloadUrl).ConfigureAwait(false))
                await using (var dst = File.Create(tempPath))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }

                PluginPackagingContract.AssertZipMatchesPolicy(tempPath, policy);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
        finally
        {
            if (ownedClient) http.Dispose();
        }
    }

    // -- helpers -------------------------------------------------------------

    private static bool IsDefaultTree(string? target) =>
        string.Equals(target, "main", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target, "master", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient(string repo)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{repo}-tests/1.0");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    private static async Task<JsonElement?> TryGetReleasesAsync(HttpClient http, string owner, string repo)
    {
        try
        {
            using var response = await http
                .GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30")
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            // Clone so the JsonElement survives the using-scope.
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
