using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Hosting;

/// <summary>
/// xUnit collection fixture that boots a real Lidarr container with a streaming
/// plugin DLL mounted into the configured plugin path, waits for the API to
/// become healthy, and exposes the API key + HTTP client to all tests in the
/// fixture's collection.
///
/// Container startup happens exactly once per test run. The fixture self-skips
/// (sets <see cref="SkipReason"/>) when Docker isn't available, the plugin DLL
/// hasn't been built, or the build is missing host-bridge artifacts.
///
/// Wave 22a — lifted from tidalarr's wave-21 in-class harness into common's
/// TestKit. Per-plugin knobs live in <see cref="LidarrContainerOptions"/>.
/// </summary>
public class LidarrContainerFixture : IAsyncLifetime
{
    private readonly LidarrContainerOptions _options;

    /// <summary>HTTP client pre-configured with a 10s timeout.</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Lidarr API key extracted from <c>/initialize.json</c> after startup.</summary>
    public string? ApiKey { get; private set; }

    /// <summary>Base URL of the running Lidarr instance.</summary>
    public string BaseUrl => $"http://localhost:{_options.LidarrPort}";

    /// <summary>The options this fixture was constructed with.</summary>
    public LidarrContainerOptions Options => _options;

    /// <summary>
    /// When non-null, all tests in the collection should call <c>Skip.If</c>
    /// against this value — the fixture wasn't able to bring up a container.
    /// </summary>
    public string? SkipReason { get; private set; }

    private bool _containerStarted;

    public LidarrContainerFixture(LidarrContainerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        options.Validate();
        _options = options;
    }

    public async Task InitializeAsync()
    {
        if (!DockerAvailable())
        {
            SkipReason = "Docker engine not running";
            return;
        }

        string repoRoot = FindRepoRoot(_options.RepoRootMarkerFile);
        string? dllPath = _options.FindPluginDll(repoRoot);
        if (dllPath is null)
        {
            SkipReason = $"Plugin DLL ({_options.PluginDllFileName}) not found. Build the plugin first.";
            return;
        }

        if (!IsHostBridgeBuild(dllPath))
        {
            SkipReason = "Plugin built without host-bridge artifacts. " +
                         "E2E requires Lidarr.Plugin.Abstractions.dll to sit alongside the plugin DLL.";
            return;
        }

        // Forcefully remove any leftover container from a previous run
        RunDocker($"rm -f {_options.ContainerName}");

        string pluginDir = Path.GetDirectoryName(dllPath)!.Replace("\\", "/");
        string runArgs =
            $"run -d --name {_options.ContainerName} " +
            $"-p {_options.LidarrPort}:8686 " +
            $"-v \"{pluginDir}:{_options.PluginMountPath}\" " +
            _options.DockerImage;

        (int exitCode, string output) = RunDocker(runArgs);
        if (exitCode != 0)
        {
            SkipReason = $"docker run failed (exit {exitCode}): {output}";
            return;
        }

        _containerStarted = true;

        try
        {
            ApiKey = await WaitForLidarrStartupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SkipReason = $"Lidarr did not become healthy: {ex.Message}";
        }
    }

    public Task DisposeAsync()
    {
        if (_containerStarted)
        {
            try { RunDocker($"rm -f {_options.ContainerName}"); } catch { /* best effort */ }
            _containerStarted = false;
        }

        Http.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Returns the running container's stdout+stderr (best effort).</summary>
    public string GetContainerLogs()
    {
        (_, string logs) = RunDocker($"logs {_options.ContainerName}");
        return logs;
    }

    // -- Internal helpers ------------------------------------------------

    private async Task<string> WaitForLidarrStartupAsync()
    {
        string initUrl = $"{BaseUrl}/initialize.json";
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));
        string? apiKey = null;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                if (apiKey is null)
                {
                    string initJson = await Http.GetStringAsync(initUrl, cts.Token).ConfigureAwait(false);
                    using JsonDocument initDoc = JsonDocument.Parse(initJson);
                    if (initDoc.RootElement.TryGetProperty("apiKey", out JsonElement apiKeyEl))
                    {
                        apiKey = apiKeyEl.GetString();
                    }
                }

                if (apiKey is not null)
                {
                    string statusUrl = $"{BaseUrl}/api/v1/system/status?apikey={apiKey}";
                    using HttpResponseMessage response = await Http.GetAsync(statusUrl, cts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return apiKey;
                    }
                }
            }
            catch when (!cts.Token.IsCancellationRequested)
            {
                // Lidarr not ready — retry
            }

            await Task.Delay(1000, cts.Token).ConfigureAwait(false);
        }

        (_, string logs) = RunDocker($"logs {_options.ContainerName}");
        throw new TimeoutException(
            $"Lidarr did not start within {_options.StartupTimeoutSeconds}s. Container logs:\n{Truncate(logs, 3000)}");
    }

    /// <summary>Virtual so unit tests can mock the docker invocation.</summary>
    protected virtual bool DockerAvailable()
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            bool exited = process.WaitForExit(10_000);
            return exited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the plugin DLL is loadable by Lidarr's host loader. Two valid shapes
    /// post-multi-plugin-co-existence-fix (PR #485, see docs/dev-guide/ALC_MULTIPLUGIN_FIX.md):
    /// 1. Merged build: Abstractions has been ILRepacked + internalized into the plugin DLL.
    ///    The merged DLL has NO AssemblyRef to <c>Lidarr.Plugin.Abstractions</c>. This is the
    ///    new normal — packages no longer ship Abstractions as a sidecar.
    /// 2. Legacy/dev build: Abstractions sits next to the plugin DLL. Tolerated for dev
    ///    iteration; Lidarr's host can still resolve the AssemblyRef from the plugin's
    ///    directory (single-plugin only — multi-plugin would fail with COR_E_INVALIDOPERATION).
    /// Pre-fix this method only checked shape (2) and silently skipped every E2E test once
    /// shape (1) shipped. The check now accepts either shape, which restores E2E coverage
    /// while still gating on actual loadability.
    /// </summary>
    private static bool IsHostBridgeBuild(string dllPath)
    {
        string dir = Path.GetDirectoryName(dllPath)!;
        if (File.Exists(Path.Combine(dir, "Lidarr.Plugin.Abstractions.dll")))
        {
            return true; // shape 2 — sidecar present
        }

        // shape 1 — merged build. Verify the plugin DLL has NO Abstractions AssemblyRef
        // (because it's been internalized) by reading its metadata. Tolerate transient
        // I/O failures by returning true (let the actual load attempt fail with a real error).
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var arh in md.AssemblyReferences)
            {
                var ar = md.GetAssemblyReference(arh);
                if (string.Equals(md.GetString(ar.Name), "Lidarr.Plugin.Abstractions", StringComparison.Ordinal))
                {
                    return false; // unresolvable: refs Abstractions but no sidecar present
                }
            }
            return true; // merged build, no external Abstractions ref → host can load it
        }
        catch
        {
            return true; // PE read failed; defer to actual load attempt
        }
    }

    /// <summary>Runs a Docker CLI command. Virtual so unit tests can avoid invoking the host binary.</summary>
    protected virtual (int ExitCode, string Output) RunDocker(string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        string combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (process.ExitCode, combined.Trim());
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "... (truncated)";

    private static string FindRepoRoot(string? markerFile)
    {
        if (string.IsNullOrEmpty(markerFile))
        {
            return AppContext.BaseDirectory;
        }

        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, markerFile)))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return AppContext.BaseDirectory;
    }
}
