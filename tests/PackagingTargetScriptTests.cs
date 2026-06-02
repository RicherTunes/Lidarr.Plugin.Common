using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Guards the cross-platform packaging helpers extracted from
    /// <c>build/PluginPackaging.targets</c>:
    ///   - <c>build/Inject-PluginBuildMetadata.ps1</c>
    ///   - <c>build/Test-PackageClosure.ps1</c>
    ///
    /// These were previously inline <c>pwsh -Command</c> strings inside MSBuild Exec tasks.
    /// On Linux MSBuild runs the command through /bin/sh, which expanded the PowerShell
    /// <c>$variable</c> sigils as empty shell variables before pwsh parsed them — corrupting
    /// <c>$manifest | Add-Member</c> into <c>| Add-Member</c> ("empty pipe element") and
    /// blanking the closure check. Moving the logic into <c>.ps1</c> files invoked via
    /// <c>pwsh -File</c> (paths passed as parameters) fixes that. These tests run on Linux CI
    /// and Windows alike, exercising the scripts directly.
    /// </summary>
    [Collection("ExternalProcess")]
    public class PackagingTargetScriptTests
    {
        private static string RepoRoot => GetRepoRoot();

        [Fact]
        public async Task Inject_AddsGitShaAndBuildTimestamp_PreservingExistingFields()
        {
            using var dir = new TempDir();
            var manifest = Path.Combine(dir.Path, "plugin.json");
            var original = new { id = "test", name = "Test", version = "1.2.3" };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(original));

            const string sha = "abc12345";
            const string timestamp = "2026-05-28T00:00:00.0000000Z";

            var (code, stdout, stderr) = await RunPwshAsync(
                "build/Inject-PluginBuildMetadata.ps1",
                $"-ManifestPath \"{manifest}\" -GitSha \"{sha}\" -BuildTimestamp \"{timestamp}\"");

            Assert.True(code == 0, $"Expected exit 0 but got {code}.\nSTDOUT:{stdout}\nSTDERR:{stderr}");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            var root = doc.RootElement;
            Assert.Equal(sha, root.GetProperty("gitSha").GetString());
            Assert.Equal(timestamp, root.GetProperty("buildTimestamp").GetString());
            // Existing fields preserved.
            Assert.Equal("test", root.GetProperty("id").GetString());
            Assert.Equal("Test", root.GetProperty("name").GetString());
            Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        }

        [Fact]
        public async Task Inject_OverwritesExistingMetadata_OnRebuild()
        {
            using var dir = new TempDir();
            var manifest = Path.Combine(dir.Path, "plugin.json");
            var original = new { id = "test", version = "1.0.0", gitSha = "stale", buildTimestamp = "1999-01-01T00:00:00Z" };
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(original));

            var (code, stdout, stderr) = await RunPwshAsync(
                "build/Inject-PluginBuildMetadata.ps1",
                $"-ManifestPath \"{manifest}\" -GitSha \"fresh123\" -BuildTimestamp \"2026-05-28T12:00:00.0000000Z\"");

            Assert.True(code == 0, $"Expected exit 0 but got {code}.\nSTDOUT:{stdout}\nSTDERR:{stderr}");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            Assert.Equal("fresh123", doc.RootElement.GetProperty("gitSha").GetString());
            Assert.Equal("2026-05-28T12:00:00.0000000Z", doc.RootElement.GetProperty("buildTimestamp").GetString());
        }

        [Fact]
        public async Task Closure_CleanOutputDir_ExitsZero()
        {
            using var dir = new TempDir();
            // Only an allowed plugin DLL present.
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "Lidarr.Plugin.Foo.dll"), "x");

            var spec = Path.Combine(RepoRoot, "scripts", "parity-spec.json");
            var (code, stdout, stderr) = await RunPwshAsync(
                "build/Test-PackageClosure.ps1",
                $"-ParitySpecPath \"{spec}\" -OutputDir \"{dir.Path}\"");

            Assert.True(code == 0, $"Expected exit 0 for a clean dir but got {code}.\nSTDOUT:{stdout}\nSTDERR:{stderr}");
            Assert.Contains("no forbidden DLLs", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Closure_ForbiddenDll_ExitsNonZero_AndNamesOffender()
        {
            using var dir = new TempDir();
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "Lidarr.Plugin.Foo.dll"), "x");
            // A host-provided assembly that must never ship inside a plugin package.
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "FluentValidation.dll"), "x");

            var spec = Path.Combine(RepoRoot, "scripts", "parity-spec.json");
            var (code, stdout, stderr) = await RunPwshAsync(
                "build/Test-PackageClosure.ps1",
                $"-ParitySpecPath \"{spec}\" -OutputDir \"{dir.Path}\"");

            Assert.True(code != 0, $"Expected non-zero exit when a forbidden DLL is present but got {code}.\nSTDOUT:{stdout}\nSTDERR:{stderr}");
            Assert.Contains("FluentValidation.dll", stdout + stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FORBIDDEN", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Regression guard for the original Linux bug: the packaging targets must invoke the
        /// helpers via <c>-File</c>, never via an inline <c>-Command</c> string with PowerShell
        /// <c>$variables</c> (which /bin/sh expands and corrupts on Linux).
        /// </summary>
        [Fact]
        public void Targets_InvokeScriptsViaFile_NotInlineCommand()
        {
            var targets = File.ReadAllText(Path.Combine(RepoRoot, "build", "PluginPackaging.targets"));

            Assert.Contains("Inject-PluginBuildMetadata.ps1", targets);
            Assert.Contains("Test-PackageClosure.ps1", targets);
            Assert.Contains("-File", targets);

            // The corrupted-on-Linux inline form must not return.
            Assert.DoesNotContain("-Command &quot;$manifest", targets);
            Assert.DoesNotContain("-Command &quot;$spec", targets);
        }

        /// <summary>
        /// The Windows-only <c>powershell</c> fallbacks must be guarded by an OS check so they
        /// never run on Linux/macOS (where <c>powershell</c> does not exist → "powershell: not found").
        /// </summary>
        [Fact]
        public void Targets_PowershellFallbacks_AreWindowsOnly()
        {
            var targets = File.ReadAllText(Path.Combine(RepoRoot, "build", "PluginPackaging.targets"));

            // Each `powershell` (Windows PowerShell) fallback Exec must be paired with a
            // Windows_NT OS guard on its Condition so it never runs on Linux/macOS.
            var powershellCount = CountOccurrences(targets, "powershell -NoProfile");
            var windowsGuardCount = CountOccurrences(targets, "'$(OS)' == 'Windows_NT'");
            Assert.True(powershellCount > 0, "Expected at least one `powershell` fallback Exec in the targets.");
            Assert.True(
                windowsGuardCount >= powershellCount,
                $"Expected at least {powershellCount} Windows_NT guards (one per powershell fallback) " +
                $"but found {windowsGuardCount}. Each `powershell` fallback must be guarded by '$(OS)' == 'Windows_NT'.");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Behavioral checks through real MSBuild — exercises the Exec → pwsh -File path
        // that broke on Linux. These run inside the main dotnet-test lane (ubuntu + windows).
        // ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Targets_InjectMetadata_ViaMsBuild_InjectsGitShaAndTimestamp()
        {
            using var dir = new TempDir();
            var manifest = Path.Combine(dir.Path, "plugin.json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { id = "test", version = "1.0.0" }));

            // Invoking InjectPluginBuildMetadata also runs ValidatePackageClosure (AfterTargets);
            // the dir holds only plugin.json so the closure gate passes (no forbidden DLLs).
            var (code, stdout, stderr) = await RunMsBuildAsync(
                "InjectPluginBuildMetadata",
                $"{dir.Path}{Path.DirectorySeparatorChar}",
                "-p:PluginManifestFileName=plugin.json");

            Assert.True(code == 0, $"Expected exit 0 but got {code}.\n{stdout}\n{stderr}");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            Assert.True(doc.RootElement.TryGetProperty("gitSha", out var sha) && !string.IsNullOrWhiteSpace(sha.GetString()),
                $"plugin.json was not given a gitSha — the inline-command sh-expansion bug has regressed.\n{stdout}\n{stderr}");
            Assert.True(doc.RootElement.TryGetProperty("buildTimestamp", out var ts) && !string.IsNullOrWhiteSpace(ts.GetString()),
                $"plugin.json was not given a buildTimestamp.\n{stdout}\n{stderr}");
        }

        [Fact]
        public async Task Targets_ValidateClosure_ViaMsBuild_PassesForCleanDir()
        {
            using var dir = new TempDir();
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "Test.Plugin.dll"), "x");

            var (code, stdout, stderr) = await RunMsBuildAsync(
                "ValidatePackageClosure",
                $"{dir.Path}{Path.DirectorySeparatorChar}");

            Assert.True(code == 0, $"Expected exit 0 for a clean dir but got {code}.\n{stdout}\n{stderr}");
        }

        [Fact]
        public async Task Targets_ValidateClosure_ViaMsBuild_FailsForForbiddenDll()
        {
            using var dir = new TempDir();
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "Test.Plugin.dll"), "x");
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "FluentValidation.dll"), "x");

            var (code, stdout, stderr) = await RunMsBuildAsync(
                "ValidatePackageClosure",
                $"{dir.Path}{Path.DirectorySeparatorChar}");

            Assert.True(code != 0, $"Expected non-zero exit when a forbidden DLL is present but got {code}.\n{stdout}\n{stderr}");
            Assert.Contains("FORBIDDEN", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<(int code, string stdout, string stderr)> RunMsBuildAsync(string target, string targetDir, params string[] extraProps)
        {
            var targetsFile = Path.Combine(RepoRoot, "build", "PluginPackaging.targets");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot
            };
            // Avoid lingering MSBuild nodes that keep the redirected pipe open past process
            // exit (the Windows-CI file-lock/hang class).
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            psi.ArgumentList.Add("msbuild");
            psi.ArgumentList.Add(targetsFile);
            psi.ArgumentList.Add($"-t:{target}");
            psi.ArgumentList.Add("-p:PluginPackagingDisable=false");
            psi.ArgumentList.Add($"-p:TargetDir={targetDir}");
            psi.ArgumentList.Add("-p:AssemblyName=Test");
            foreach (var p in extraProps) psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("-nologo");
            psi.ArgumentList.Add("-v:minimal");

            using var p2 = new Process { StartInfo = psi };
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();
            p2.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
            p2.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
            p2.Start();
            p2.BeginOutputReadLine();
            p2.BeginErrorReadLine();

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await p2.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p2.Kill(entireProcessTree: true); } catch { } return (-1, sbOut.ToString(), "msbuild timed out after 120s"); }

            return (p2.ExitCode, sbOut.ToString(), sbErr.ToString());
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

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return (-1, sbOut.ToString(), "Process timed out after 60s"); }

            return (p.ExitCode, sbOut.ToString(), sbErr.ToString());
        }

        private static string GetRepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 6; i++)
            {
                if (File.Exists(Path.Combine(dir, "build", "Test-PackageClosure.ps1"))) return dir;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null) break;
                dir = parent;
            }
            throw new DirectoryNotFoundException("Could not locate repo root containing build/Test-PackageClosure.ps1");
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
            public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
        }
    }
}
