using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class ManifestCheckScriptTests
    {
        private static string RepoRoot => GetRepoRoot();

        [Fact]
        public async Task ManifestCheck_Handles_DefaultNamespace_Project()
        {
            using var dir = new TempDir();
            var csproj = Path.Combine(dir.Path, "Test.csproj");
            var manifest = Path.Combine(dir.Path, "plugin.json");

            // MSBuild namespace project with Version + PackageReference
            var csprojXml = """
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Version>1.2.3</Version>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" />
  </ItemGroup>
</Project>
""";
            await File.WriteAllTextAsync(csproj, csprojXml);

            var manifestObj = new { version = "1.2.3", apiVersion = "1.x", minHostVersion = "10.0.0" };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

            var (code, stdout, stderr) = await RunPwshAsync("tools/ManifestCheck.ps1", $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\"");
            Assert.Equal(0, code);
            Assert.Contains("Manifest validation succeeded", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ManifestCheck_Handles_NoNamespace_Project()
        {
            using var dir = new TempDir();
            var csproj = Path.Combine(dir.Path, "Test.csproj");
            var manifest = Path.Combine(dir.Path, "plugin.json");

            // No xmlns
            var csprojXml = """
<Project>
  <PropertyGroup>
    <Version>2.0.0</Version>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" />
  </ItemGroup>
</Project>
""";
            await File.WriteAllTextAsync(csproj, csprojXml);

            var manifestObj = new { version = "2.0.0", apiVersion = "1.x", minHostVersion = "10.0.0" };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

            var (code, stdout, stderr) = await RunPwshAsync("tools/ManifestCheck.ps1", $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\"");
            Assert.Equal(0, code);
            Assert.Contains("Manifest validation succeeded", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ManifestCheck_ProjectReference_FallsBack_To_CommonVersion_Warns_Man001()
        {
            using var dir = new TempDir();
            var csproj = Path.Combine(dir.Path, "Test.csproj");
            var manifest = Path.Combine(dir.Path, "plugin.json");

            var csprojXml = """
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Version>3.0.0</Version>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\src\Abstractions\Lidarr.Plugin.Abstractions.csproj" />
  </ItemGroup>
</Project>
""";
            await File.WriteAllTextAsync(csproj, csprojXml);

            var manifestObj = new { version = "3.0.0", apiVersion = "1.x", commonVersion = "1.2.3" };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

            var (code, stdout, stderr) = await RunPwshAsync("tools/ManifestCheck.ps1", $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\"");
            Assert.Equal(0, code); // warning only
            Assert.Contains("MAN001", stdout + stderr, StringComparison.OrdinalIgnoreCase);

            // Strict should fail
            var (codeStrict, so, se) = await RunPwshAsync("tools/ManifestCheck.ps1", $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -Strict");
            Assert.NotEqual(0, codeStrict);
            Assert.Contains("MAN001", so + se, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ManifestCheck_ValidateEntryPoints_Succeeds_When_Type_Exists()
        {
            using var dir = new TempDir();
            var csproj = await CreateBuildablePluginProjectAsync(dir.Path, includeEntryPointType: true);
            var manifest = await WriteManifestAsync(dir.Path, implementationType: "Lidarr.Plugin.TestEntry.EntryPoint");

            await DotnetBuildAsync(csproj);

            var (code, stdout, stderr) = await RunPwshAsync(
                "tools/ManifestCheck.ps1",
                $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -ValidateEntryPoints -Configuration Release -TargetFramework net8.0");

            Assert.Equal(0, code);
            Assert.Contains("Manifest validation succeeded", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ManifestCheck_ValidateEntryPoints_Fails_With_Ent001_When_Type_Missing()
        {
            using var dir = new TempDir();
            var csproj = await CreateBuildablePluginProjectAsync(dir.Path, includeEntryPointType: false);
            var manifest = await WriteManifestAsync(dir.Path, implementationType: "Lidarr.Plugin.TestEntry.MissingEntryPoint");

            await DotnetBuildAsync(csproj);

            var (code, stdout, stderr) = await RunPwshAsync(
                "tools/ManifestCheck.ps1",
                $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -ValidateEntryPoints -Configuration Release -TargetFramework net8.0");

            Assert.NotEqual(0, code);
            Assert.Contains("ENT001", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> CreateBuildablePluginProjectAsync(string root, bool includeEntryPointType)
        {
            var abstractionsDir = Path.Combine(root, "Abstractions");
            Directory.CreateDirectory(abstractionsDir);

            var dummyAbstractions = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Dummy.Abstractions</AssemblyName>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""";
            await File.WriteAllTextAsync(Path.Combine(abstractionsDir, "Dummy.Abstractions.csproj"), dummyAbstractions);

            var csprojPath = Path.Combine(root, "Lidarr.Plugin.TestEntry.csproj");
            var csprojXml = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AssemblyName>Lidarr.Plugin.TestEntry</AssemblyName>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="Abstractions/Dummy.Abstractions.csproj" />
  </ItemGroup>
</Project>
""";
            await File.WriteAllTextAsync(csprojPath, csprojXml);

            var otherTypeSource = """
namespace Lidarr.Plugin.TestEntry;

public sealed class OtherType { }
""";
            await File.WriteAllTextAsync(Path.Combine(root, "OtherType.cs"), otherTypeSource);

            var entryPointSource = """
namespace Lidarr.Plugin.TestEntry;

public sealed class EntryPoint { }
""";
            if (includeEntryPointType)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "EntryPoint.cs"), entryPointSource);
            }

            return csprojPath;
        }

        private static async Task<string> WriteManifestAsync(string root, string implementationType)
        {
            var manifestPath = Path.Combine(root, "plugin.json");
            var manifestObj = new
            {
                version = "1.0.0",
                apiVersion = "1.x",
                commonVersion = "1.0.0",
                minHostVersion = "10.0.0",
                assemblies = new[] { "Lidarr.Plugin.TestEntry.dll" },
                entryPoints = new[]
                {
                    new { name = "Test", implementation = implementationType }
                }
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifestObj));
            return manifestPath;
        }

        private static async Task DotnetBuildAsync(string projectPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" -c Release -nologo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!
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

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet build failed for '{projectPath}'.\n{sbOut}\n{sbErr}");
            }
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
            public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
            public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
        }
    }
}
