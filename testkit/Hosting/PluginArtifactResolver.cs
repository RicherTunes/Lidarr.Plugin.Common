using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lidarr.Plugin.Common.TestKit.Hosting;

/// <summary>
/// Resolves the plugin DLL directory that Docker E2E fixtures should mount.
/// Prefer package-closure artifacts over raw build output so test-only builds
/// that contain host-boundary sidecars do not mask production packaging rules.
/// </summary>
public static class PluginArtifactResolver
{
    /// <summary>
    /// Host-provided assemblies that must not sit beside a plugin DLL mounted
    /// into Lidarr. These DLLs cross plugin/host contracts and create type
    /// identity failures when a raw build output directory is mounted.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenHostBoundarySidecars =
        new[]
        {
            "FluentValidation.dll",
            "Lidarr.Plugin.Common.dll",
            "Lidarr.Plugin.Abstractions.dll",
            "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll",
            "Microsoft.Extensions.Logging.dll",
            "Microsoft.Extensions.Configuration.dll",
            "Microsoft.Extensions.Caching.Memory.dll",
            "Microsoft.Extensions.Http.dll",
            "NLog.dll",
            "System.Text.Json.dll",
            "Lidarr.Core.dll",
            "Lidarr.Http.dll",
            "Lidarr.Api.V1.dll",
            "Lidarr.Common.dll",
            "NzbDrone.Common.dll",
            "NzbDrone.Core.dll",
            "NzbDrone.SignalR.dll",
        };

    /// <summary>
    /// Finds the first usable plugin DLL. The canonical packaged publish
    /// artifact and legacy <c>package/</c> directory are always checked before
    /// plugin-specific raw build candidates. If the first existing candidate
    /// is dirty, the resolver fails closed instead of masking the packaging
    /// problem with a lower-priority fallback.
    /// </summary>
    public static string? FindPluginDll(
        string repoRoot,
        string pluginDllFileName,
        params string[] additionalCandidates)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new ArgumentException("Repository root must not be empty.", nameof(repoRoot));
        }

        if (string.IsNullOrWhiteSpace(pluginDllFileName))
        {
            throw new ArgumentException("Plugin DLL file name must not be empty.", nameof(pluginDllFileName));
        }

        foreach (var candidate in EnumerateCandidatePaths(repoRoot, pluginDllFileName, additionalCandidates))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (HasForbiddenHostBoundarySidecars(candidate))
            {
                return null;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Returns true when the directory containing <paramref name="pluginDllPath"/>
    /// includes a sidecar that should be supplied by Lidarr or internalized into
    /// the merged plugin assembly.
    /// </summary>
    public static bool HasForbiddenHostBoundarySidecars(string pluginDllPath)
    {
        var pluginDir = Path.GetDirectoryName(pluginDllPath);
        if (string.IsNullOrWhiteSpace(pluginDir))
        {
            return true;
        }

        return ForbiddenHostBoundarySidecars.Any(name => File.Exists(Path.Combine(pluginDir, name)));
    }

    private static IEnumerable<string> EnumerateCandidatePaths(
        string repoRoot,
        string pluginDllFileName,
        IReadOnlyList<string>? additionalCandidates)
    {
        var candidates = new List<string>
        {
            Path.Combine(repoRoot, "artifacts", "publish", "net8.0", "Release", pluginDllFileName),
            Path.Combine(repoRoot, "package", pluginDllFileName),
        };

        if (additionalCandidates is not null)
        {
            candidates.AddRange(additionalCandidates.Select(candidate =>
                Path.IsPathRooted(candidate) ? candidate : Path.Combine(repoRoot, candidate)));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }
}
