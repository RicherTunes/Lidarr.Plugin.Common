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
        public async Task ManifestCheck_Warns_On_Legacy_Keys_Man004()
        {
            using var dir = new TempDir();
            var csproj = Path.Combine(dir.Path, "Test.csproj");
            var manifest = Path.Combine(dir.Path, "plugin.json");

            var csprojXml = """
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Version>4.0.0</Version>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" />
  </ItemGroup>
</Project>
""";
            await File.WriteAllTextAsync(csproj, csprojXml);

            var manifestObj = new
            {
                version = "4.0.0",
                apiVersion = "1.x",
                minHostVersion = "10.0.0",
                minimumVersion = "10.0.0"
            };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(manifestObj));

            var (code, stdout, stderr) = await RunPwshAsync("tools/ManifestCheck.ps1", $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\"");
            Assert.Equal(0, code);
            Assert.Contains("MAN004", stdout + stderr, StringComparison.OrdinalIgnoreCase);
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
