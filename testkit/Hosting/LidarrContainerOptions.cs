using System;

namespace Lidarr.Plugin.Common.TestKit.Hosting;

/// <summary>
/// Per-plugin configuration for <see cref="LidarrContainerFixture"/>.
/// Wave 22a — lifted from tidalarr's wave-21 inline fixture so the same Docker
/// E2E orchestrator powers every streaming plugin.
/// </summary>
/// <param name="DockerImage">
/// Lidarr Docker image tag, e.g. <c>ghcr.io/hotio/lidarr:pr-plugins-3.1.2.4913</c>.
/// MUST be a .NET 8 plugins-branch build (<c>pr-plugins-3.x</c>) — .NET 6 images
/// (<c>pr-plugins-2.x</c>) crash-loop on plugin load.
/// </param>
/// <param name="ContainerName">
/// Docker container name. Make this unique per plugin (e.g. <c>tidalarr-e2e</c>,
/// <c>qobuzarr-e2e</c>) so concurrent harnesses across plugins do not collide.
/// </param>
/// <param name="LidarrPort">
/// Host port to publish Lidarr's 8686 on. Use a single-plugin instance per
/// plugin to avoid the upstream Lidarr AssemblyLoadContext lifecycle bug.
/// </param>
/// <param name="PluginMountPath">
/// Container-side mount path for the plugin DLL directory, e.g.
/// <c>/config/plugins/RicherTunes/Tidalarr</c>.
/// </param>
/// <param name="PluginDllFileName">
/// Plugin DLL filename used for host-bridge sniff and discovery (e.g.
/// <c>Lidarr.Plugin.Tidalarr.dll</c>).
/// </param>
/// <param name="FindPluginDll">
/// Resolver invoked at fixture startup. Receives <c>repoRoot</c> (best-effort
/// directory containing the plugin's solution file) and the build configuration
/// label, returns absolute path to the merged plugin DLL or <c>null</c> when it
/// hasn't been built. The fixture self-skips when this returns <c>null</c>.
/// </param>
/// <param name="PluginEntrySubstring">
/// Substring matched against <c>name</c> and <c>implementation</c> of entries
/// in <c>/api/v1/indexer/schema</c> and <c>/api/v1/downloadclient/schema</c>
/// (case-insensitive) — e.g. <c>"Tidal"</c>, <c>"Qobuz"</c>, <c>"AppleMusic"</c>.
/// </param>
/// <param name="RepoRootMarkerFile">
/// Optional marker file the fixture walks up to discover the plugin repo root
/// (passed into <see cref="FindPluginDll"/>). Defaults to <c>null</c>, in which
/// case <c>AppContext.BaseDirectory</c> is used as-is.
/// </param>
/// <param name="StartupTimeoutSeconds">
/// Seconds to wait for Lidarr to become healthy after <c>docker run</c>. Default 90.
/// </param>
public sealed record LidarrContainerOptions(
    string DockerImage,
    string ContainerName,
    int LidarrPort,
    string PluginMountPath,
    string PluginDllFileName,
    Func<string, string?> FindPluginDll,
    string PluginEntrySubstring,
    string? RepoRootMarkerFile = null,
    int StartupTimeoutSeconds = 90)
{
    /// <summary>
    /// Validates required fields. Throws <see cref="ArgumentException"/> on
    /// any null/empty knob — better to fail fast at fixture construction than
    /// during a slow <c>docker run</c>.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DockerImage))
            throw new ArgumentException("DockerImage must not be empty.", nameof(DockerImage));
        if (string.IsNullOrWhiteSpace(ContainerName))
            throw new ArgumentException("ContainerName must not be empty.", nameof(ContainerName));
        if (LidarrPort <= 0 || LidarrPort > 65535)
            throw new ArgumentException($"LidarrPort {LidarrPort} is out of range.", nameof(LidarrPort));
        if (string.IsNullOrWhiteSpace(PluginMountPath))
            throw new ArgumentException("PluginMountPath must not be empty.", nameof(PluginMountPath));
        if (string.IsNullOrWhiteSpace(PluginDllFileName))
            throw new ArgumentException("PluginDllFileName must not be empty.", nameof(PluginDllFileName));
        if (FindPluginDll is null)
            throw new ArgumentException("FindPluginDll must not be null.", nameof(FindPluginDll));
        if (string.IsNullOrWhiteSpace(PluginEntrySubstring))
            throw new ArgumentException("PluginEntrySubstring must not be empty.", nameof(PluginEntrySubstring));
        if (StartupTimeoutSeconds <= 0)
            throw new ArgumentException("StartupTimeoutSeconds must be positive.", nameof(StartupTimeoutSeconds));
    }
}
