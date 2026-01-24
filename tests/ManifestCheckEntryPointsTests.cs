using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public class ManifestCheckEntryPointsTests
{
    private static string RepoRoot => GetRepoRoot();

    [Fact]
    public async Task Should_Resolve_EntryPoints_When_Implementations_Exist()
    {
        using var dir = new TempDir();
        var csproj = Path.Combine(dir.Path, "Test.csproj");
        var manifest = Path.Combine(dir.Path, "manifest.json");
        var source = Path.Combine(dir.Path, "EntryPointType.cs");

        await File.WriteAllTextAsync(
            source,
            """
            namespace My.Plugin;
            public sealed class EntryPointType { }
            """);

        var abstractionsCsproj = Path.Combine(RepoRoot, "src", "Abstractions", "Lidarr.Plugin.Abstractions.csproj");
        var csprojXml = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <Version>1.0.0</Version>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{EscapeForXml(abstractionsCsproj)}" />
          </ItemGroup>
        </Project>
        """;
        await File.WriteAllTextAsync(csproj, csprojXml);

        await DotnetBuildAsync(csproj, dir.Path);

        var assemblyPath = Path.Combine(dir.Path, "bin", "Release", "net8.0", "Test.dll");
        Assert.True(File.Exists(assemblyPath), $"Expected test assembly at {assemblyPath}");

        var manifestObj = new
        {
            version = "1.0.0",
            apiVersion = "1.x",
            commonVersion = "1.0.0",
            minHostVersion = "10.0.0",
            entryPoints = new[]
            {
                new { type = "Indexer", implementation = "My.Plugin.EntryPointType" }
            }
        };
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

        var (code, stdout, stderr) = await RunPwshAsync(
            "tools/ManifestCheck.ps1",
            $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -ResolveEntryPoints -EntryAssemblyPath \"{assemblyPath}\"");
        Assert.Equal(0, code);
        Assert.Contains("Manifest validation succeeded", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_Fail_With_MAN002_When_EntryPoint_Type_Is_Missing()
    {
        using var dir = new TempDir();
        var csproj = Path.Combine(dir.Path, "Test.csproj");
        var manifest = Path.Combine(dir.Path, "manifest.json");
        var source = Path.Combine(dir.Path, "EntryPointType.cs");

        await File.WriteAllTextAsync(
            source,
            """
            namespace My.Plugin;
            public sealed class EntryPointType { }
            """);

        var abstractionsCsproj = Path.Combine(RepoRoot, "src", "Abstractions", "Lidarr.Plugin.Abstractions.csproj");
        var csprojXml = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <Version>1.0.0</Version>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{EscapeForXml(abstractionsCsproj)}" />
          </ItemGroup>
        </Project>
        """;
        await File.WriteAllTextAsync(csproj, csprojXml);

        await DotnetBuildAsync(csproj, dir.Path);

        var assemblyPath = Path.Combine(dir.Path, "bin", "Release", "net8.0", "Test.dll");
        Assert.True(File.Exists(assemblyPath), $"Expected test assembly at {assemblyPath}");

        var manifestObj = new
        {
            version = "1.0.0",
            apiVersion = "1.x",
            commonVersion = "1.0.0",
            minHostVersion = "10.0.0",
            entryPoints = new[]
            {
                new { type = "Indexer", implementation = "My.Plugin.MissingType" }
            }
        };
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

        var (code, stdout, stderr) = await RunPwshAsync(
            "tools/ManifestCheck.ps1",
            $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -ResolveEntryPoints -EntryAssemblyPath \"{assemblyPath}\"");
        Assert.NotEqual(0, code);
        Assert.Contains("MAN002", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_Fail_With_MAN003_When_ResolveEntryPoints_Is_Requested_But_AssemblyPath_Is_Missing()
    {
        using var dir = new TempDir();
        var csproj = Path.Combine(dir.Path, "Test.csproj");
        var manifest = Path.Combine(dir.Path, "manifest.json");

        var abstractionsCsproj = Path.Combine(RepoRoot, "src", "Abstractions", "Lidarr.Plugin.Abstractions.csproj");
        var csprojXml = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <Version>1.0.0</Version>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{EscapeForXml(abstractionsCsproj)}" />
          </ItemGroup>
        </Project>
        """;
        await File.WriteAllTextAsync(csproj, csprojXml);

        var manifestObj = new
        {
            version = "1.0.0",
            apiVersion = "1.x",
            commonVersion = "1.0.0",
            minHostVersion = "10.0.0",
            entryPoints = new[]
            {
                new { type = "Indexer", implementation = "My.Plugin.EntryPointType" }
            }
        };
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

        var (code, stdout, stderr) = await RunPwshAsync(
            "tools/ManifestCheck.ps1",
            $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -ResolveEntryPoints");
        Assert.NotEqual(0, code);
        Assert.Contains("MAN003", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DotnetBuildAsync(string csproj, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{csproj}\" -c Release -v minimal",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (s, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"dotnet build failed ({p.ExitCode}):\n{so}\n{se}");
    }

    private static async Task<(int code, string stdout, string stderr)> RunPwshAsync(string scriptRelative, string args)
    {
        var script = Path.Combine(RepoRoot, scriptRelative);
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot
        };
        using var p = new Process { StartInfo = psi };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();
        p.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return (p.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    private static string EscapeForXml(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;");

    private static string GetRepoRoot()
    {
        // Walk up from test bin to find 'tools/ManifestCheck.ps1'
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(dir, "tools", "ManifestCheck.ps1"))) return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root containing tools/ManifestCheck.ps1");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-ep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}

