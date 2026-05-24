using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Declarative shape of what a Lidarr plugin's release zip must contain.
/// </summary>
/// <param name="MainDllName">
/// The plugin's main DLL (e.g. <c>"Lidarr.Plugin.Brainarr.dll"</c>). Must be present in
/// the zip; also subject to the <see cref="MainDllMinimumBytes"/> sanity check.
/// </param>
/// <param name="RequiredFiles">
/// All files that MUST be present in the zip (filenames only — paths are stripped).
/// Should include <see cref="MainDllName"/> + <c>plugin.json</c> at minimum.
/// </param>
/// <param name="ForbiddenAssemblies">
/// Filenames that MUST NOT be present. Shipping these causes type-identity conflicts
/// (host contract DLLs like FluentValidation, NLog, Microsoft.Extensions.*.Abstractions)
/// or regresses multi-plugin co-existence (sidecar Lidarr.Plugin.Abstractions/Common).
/// </param>
/// <param name="MainDllMinimumBytes">
/// Minimum size of <see cref="MainDllName"/>. For ILRepack-merged plugins this should
/// be ≥2MB — a smaller DLL means the merge didn't run and the runtime will fail with
/// "Could not load Lidarr.Plugin.Common / Abstractions" because the (correctly omitted)
/// sidecars aren't there to load. Set to <c>0</c> for sidecar-packaged plugins.
/// </param>
public sealed record PluginPackagePolicy(
    string MainDllName,
    IReadOnlyList<string> RequiredFiles,
    IReadOnlyList<string> ForbiddenAssemblies,
    long MainDllMinimumBytes = 0);

/// <summary>
/// Static assertions catching packaging-policy violations against a built plugin zip.
///
/// <para>Usage (merged-DLL plugin, e.g. brainarr/qobuzarr/tidalarr):</para>
/// <code>
/// var policy = PluginPackagingContract.MergedDllPolicy("Lidarr.Plugin.Brainarr",
///     extraRequired: ["manifest.json", ".lidarr.plugin"]);
/// PluginPackagingContract.AssertZipMatchesPolicy(zipPath, policy);
/// </code>
///
/// <para>Note on naming: <c>MainDllName</c> MUST start with <c>"Lidarr.Plugin."</c>
/// — Lidarr's PluginLoader globs <c>Lidarr.Plugin.*.dll</c> and silently ignores
/// any other filename (see <see cref="AssertMainDllMatchesLoaderNamingConvention"/>).
/// This is enforced by <see cref="AssertZipMatchesPolicy"/>.</para>
/// </summary>
public static class PluginPackagingContract
{
    /// <summary>
    /// Host-provided contract assemblies + Lidarr/NzbDrone host assemblies that must
    /// never ship in a plugin zip — shipping them causes type-identity conflicts that
    /// surface as "Method 'Test' does not have an implementation" /
    /// "Could not load file or assembly" at runtime.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultForbiddenHostAssemblies = new[]
    {
        // Host-provided contract assemblies — type-identity conflicts if shipped
        "FluentValidation.dll",
        "NLog.dll",
        "System.Text.Json.dll",
        "Newtonsoft.Json.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Microsoft.Extensions.Caching.Abstractions.dll",
        "Microsoft.Extensions.Caching.Memory.dll",
        "Microsoft.Extensions.Options.dll",
        "Microsoft.Extensions.Primitives.dll",
        // Lidarr host assemblies — host provides them; shipping triggers loader conflicts
        "Lidarr.Core.dll",
        "Lidarr.Common.dll",
        "Lidarr.Http.dll",
        "Lidarr.Api.V1.dll",
        "Lidarr.Host.dll",
        "NzbDrone.Core.dll",
        "NzbDrone.Common.dll",
        "NzbDrone.SignalR.dll",
    };

    /// <summary>
    /// Forbidden list for ILRepack-merged plugins (brainarr/qobuzarr/tidalarr):
    /// host assemblies AS WELL AS Lidarr.Plugin.{Abstractions,Common}.dll, since those
    /// are merged + internalized into the plugin DLL. Shipping them as sidecars
    /// reintroduces the COR_E_INVALIDOPERATION cross-ALC conflict the merge fixed.
    /// </summary>
    public static readonly IReadOnlyList<string> MergedPluginForbiddenAssemblies =
        DefaultForbiddenHostAssemblies
            .Concat(new[] { "Lidarr.Plugin.Abstractions.dll", "Lidarr.Plugin.Common.dll" })
            .ToArray();

    /// <summary>
    /// Convenience constructor for ILRepack-merged plugins.
    /// </summary>
    /// <param name="mainAssemblyName">
    /// Bare assembly name (no .dll) e.g. <c>"Lidarr.Plugin.Brainarr"</c>.
    /// </param>
    /// <param name="extraRequired">
    /// Additional required filenames beyond the main DLL + <c>plugin.json</c>.
    /// Pass <c>"manifest.json"</c> here if the plugin ships one.
    /// </param>
    /// <param name="minBytes">
    /// Override the 2MB merged-DLL minimum. Pass 0 to skip the size check.
    /// </param>
    public static PluginPackagePolicy MergedDllPolicy(
        string mainAssemblyName,
        IEnumerable<string>? extraRequired = null,
        long minBytes = 2_000_000)
    {
        var mainDll = mainAssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? mainAssemblyName
            : mainAssemblyName + ".dll";

        var required = new List<string> { mainDll, "plugin.json" };
        if (extraRequired is not null) required.AddRange(extraRequired);

        return new PluginPackagePolicy(
            MainDllName: mainDll,
            RequiredFiles: required,
            ForbiddenAssemblies: MergedPluginForbiddenAssemblies,
            MainDllMinimumBytes: minBytes);
    }

    /// <summary>
    /// Lidarr's PluginLoader globs <c>Lidarr.Plugin.*.dll</c> when scanning
    /// <c>/config/plugins/&lt;owner&gt;/&lt;name&gt;/</c>
    /// (<c>NzbDrone.Common/Extensions/PathExtensions.cs:334</c>). Any DLL whose filename
    /// does NOT start with <c>"Lidarr.Plugin."</c> is silently ignored at host bootstrap —
    /// no error, no warning, no log line; the plugin just never appears in
    /// <c>/api/v1/system/plugins</c> and never registers any services.
    /// </summary>
    /// <remarks>
    /// Caught the May 2026 AppleMusicarr incident: install worked, files were on disk,
    /// but <c>AppleMusicarr.Plugin.dll</c> didn't match the glob → plugin invisible.
    /// Fix: <c>&lt;AssemblyName&gt;Lidarr.Plugin.AppleMusicarr&lt;/AssemblyName&gt;</c>.
    /// </remarks>
    public static void AssertMainDllMatchesLoaderNamingConvention(PluginPackagePolicy policy)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        Assert.True(
            policy.MainDllName.StartsWith("Lidarr.Plugin.", StringComparison.OrdinalIgnoreCase) &&
                policy.MainDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
            $"Main DLL '{policy.MainDllName}' does not match Lidarr's PluginLoader glob " +
            $"'Lidarr.Plugin.*.dll' (NzbDrone.Common/Extensions/PathExtensions.cs:334). " +
            $"Set <AssemblyName>Lidarr.Plugin.<Name></AssemblyName> in the .Plugin .csproj. " +
            $"Without this the plugin loads silently into nothing — it won't appear in " +
            $"/api/v1/system/plugins and won't register any services.");
    }

    /// <summary>
    /// Asserts the zip at <paramref name="zipPath"/> satisfies the policy:
    /// every <see cref="PluginPackagePolicy.RequiredFiles"/> is present (case-insensitive),
    /// every <see cref="PluginPackagePolicy.ForbiddenAssemblies"/> is absent,
    /// the main DLL meets the size minimum, and the main DLL filename satisfies the
    /// PluginLoader naming convention (<see cref="AssertMainDllMatchesLoaderNamingConvention"/>).
    /// </summary>
    public static void AssertZipMatchesPolicy(string zipPath, PluginPackagePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
            throw new ArgumentException("zipPath must be non-empty.", nameof(zipPath));
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Plugin zip not found.", zipPath);
        if (policy is null)
            throw new ArgumentNullException(nameof(policy));

        AssertMainDllMatchesLoaderNamingConvention(policy);

        using var archive = ZipFile.OpenRead(zipPath);

        var fileNames = archive.Entries
            .Select(e => Path.GetFileName(e.FullName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inventory = string.Join(", ", fileNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        foreach (var required in policy.RequiredFiles)
        {
            Assert.True(fileNames.Contains(required),
                $"Plugin zip at '{zipPath}' is missing required file '{required}'.\n" +
                $"Contents: {inventory}");
        }

        foreach (var forbidden in policy.ForbiddenAssemblies)
        {
            Assert.False(fileNames.Contains(forbidden),
                $"Plugin zip at '{zipPath}' ships FORBIDDEN '{forbidden}' — " +
                $"this would cause type-identity conflicts or regress multi-plugin co-existence.\n" +
                $"Contents: {inventory}");
        }

        if (policy.MainDllMinimumBytes > 0)
        {
            var mainEntry = archive.Entries.FirstOrDefault(e =>
                Path.GetFileName(e.FullName).Equals(policy.MainDllName, StringComparison.OrdinalIgnoreCase));

            Assert.True(mainEntry is not null,
                $"Main DLL '{policy.MainDllName}' must be in the zip.");

            Assert.True(mainEntry!.Length >= policy.MainDllMinimumBytes,
                $"Main DLL '{policy.MainDllName}' is only {mainEntry.Length} bytes; " +
                $"policy requires ≥{policy.MainDllMinimumBytes} bytes. " +
                $"For ILRepack-merged plugins a sub-threshold DLL means the merge didn't run " +
                $"and runtime will fail with 'Could not load Lidarr.Plugin.Common / Abstractions'.");
        }
    }
}
