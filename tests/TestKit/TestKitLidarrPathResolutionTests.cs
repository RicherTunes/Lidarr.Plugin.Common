using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.TestKit;

[Collection("ExternalProcess")]
public sealed class TestKitLidarrPathResolutionTests
{
    [Fact]
    public async Task TestKitLidarrPathResolution_PrefersDockerOutputOverSourceOutput_ForStandaloneLayout()
    {
        using var workspace = new TempDir();
        var testKitProject = CopyTestKitProject(workspace.Path, "testkit");
        var sourceOutput = CreateHostOutput(workspace.Path, includeHostDll: true, "ext", "Lidarr");
        var dockerOutput = CreateHostOutput(workspace.Path, includeHostDll: true, "ext", "Lidarr-docker");

        var resolved = await GetLidarrPathAsync(testKitProject);

        Assert.NotEqual(NormalizePath(sourceOutput), resolved);
        Assert.Equal(NormalizePath(dockerOutput), resolved);
    }

    [Fact]
    public async Task TestKitLidarrPathResolution_SkipsStaleSourceDirectory_ForSubmoduleLayout()
    {
        using var workspace = new TempDir();
        var testKitProject = CopyTestKitProject(
            workspace.Path,
            Path.Combine("plugin", "ext", "Lidarr.Plugin.Common", "testkit"));
        var staleSourceOutput = CreateHostOutput(workspace.Path, includeHostDll: false, "plugin", "ext", "Lidarr");
        var dockerOutput = CreateHostOutput(workspace.Path, includeHostDll: true, "plugin", "ext", "Lidarr-docker");

        var resolved = await GetLidarrPathAsync(testKitProject);

        Assert.NotEqual(NormalizePath(staleSourceOutput), resolved);
        Assert.Equal(NormalizePath(dockerOutput), resolved);
    }

    private static string CopyTestKitProject(string workspaceRoot, string relativeProjectDirectory)
    {
        var projectDirectory = Path.Combine(workspaceRoot, relativeProjectDirectory);
        Directory.CreateDirectory(projectDirectory);
        var sourceProject = Path.Combine(GetRepoRoot(), "testkit", "Lidarr.Plugin.Common.TestKit.csproj");
        var targetProject = Path.Combine(projectDirectory, "Lidarr.Plugin.Common.TestKit.csproj");
        File.Copy(sourceProject, targetProject);
        return targetProject;
    }

    private static string CreateHostOutput(string workspaceRoot, bool includeHostDll, params string[] relativeSegments)
    {
        var outputDirectory = Path.Combine(relativeSegments.Prepend(workspaceRoot).Append("_output").Append("net8.0").ToArray());
        Directory.CreateDirectory(outputDirectory);
        if (includeHostDll)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "Lidarr.Common.dll"), string.Empty);
        }

        return outputDirectory;
    }

    private static async Task<string> GetLidarrPathAsync(string projectPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        psi.Environment.Remove("LIDARR_PATH");
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-getProperty:LidarrPath");
        psi.ArgumentList.Add("-nologo");

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("dotnet msbuild -getProperty:LidarrPath timed out.");
        }

        Assert.True(
            process.ExitCode == 0,
            $"dotnet msbuild -getProperty:LidarrPath failed with exit {process.ExitCode}.{Environment.NewLine}STDOUT:{stdout}{Environment.NewLine}STDERR:{stderr}");
        return NormalizePath(stdout.ToString().Trim());
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "testkit", "Lidarr.Plugin.Common.TestKit.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testkit-lidarr-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
