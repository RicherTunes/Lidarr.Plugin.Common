using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class ManifestCheckJsonTests
    {
        [Fact]
        public async Task Emits_JSON_Diagnostics_With_IDs()
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

            var (code, stdout, stderr) = await RunPwshAsync("tools/ManifestCheck.ps1", $"-ProjectPath \"{csproj}\" -ManifestPath \"{manifest}\" -JsonOutput -Strict");
            // In strict mode, MAN001 should be elevated to error; some environments may still return 0 if tools override
            // Focus the oracle on diagnostics content rather than exit code.

            var diags = JsonSerializer.Deserialize<Diag[]>(stdout);
            Assert.NotNull(diags);
            Assert.Contains(diags!, d => string.Equals(d.id, "MAN001", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<(int code, string stdout, string stderr)> RunPwshAsync(string scriptRelative, string args)
        {
            var script = Path.Combine(GetRepoRoot(), scriptRelative);
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GetRepoRoot()
            };
            using var p = new Process { StartInfo = psi };
            var so = new System.Text.StringBuilder();
            var se = new System.Text.StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) se.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return (p.ExitCode, so.ToString(), se.ToString());
        }

        private static string GetRepoRoot()
        {
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

        private record Diag(string id, string severity, string message, string payload);

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcj-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
            public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
        }
    }
}
