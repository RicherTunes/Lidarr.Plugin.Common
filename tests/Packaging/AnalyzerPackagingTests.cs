using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Packaging
{
    /// <summary>
    /// Delivery-lens guard for the Roslyn analyzer NuGet package.
    ///
    /// Origin: external audit #4 — the analyzer project shipped with
    /// <c>IncludeBuildOutput=true</c> and no <c>analyzers/dotnet/cs</c> PackagePath, so
    /// <c>dotnet pack</c> placed <c>Lidarr.Plugin.Analyzers.dll</c> under <c>lib/</c>.
    /// NuGet only loads an assembly AS AN ANALYZER when it sits under
    /// <c>analyzers/dotnet/cs/</c>; anything in <c>lib/</c> is treated as a runtime
    /// reference. Net effect: every consuming plugin silently got NO analyzer, even
    /// though the analyzer's rules were unit-tested and "green".
    ///
    /// This guard actually packs the project and inspects the produced .nupkg layout
    /// (the artifact), rather than asserting csproj properties — which is precisely the
    /// "tested logic, not delivery" trap the audit identified.
    /// </summary>
    [Collection("ExternalProcess")]
    public class AnalyzerPackagingTests
    {
        private const string AnalyzerDll = "Lidarr.Plugin.Analyzers.dll";

        [Fact]
        public void Analyzer_nupkg_places_dll_under_analyzers_dotnet_cs_not_lib()
        {
            var repoRoot = FindRepoRoot();
            var csproj = Path.Combine(
                repoRoot, "tools", "Analyzers", "Lidarr.Plugin.Analyzers", "Lidarr.Plugin.Analyzers.csproj");
            Assert.True(File.Exists(csproj), $"analyzer csproj not found at {csproj}");

            var outDir = Path.Combine(Path.GetTempPath(), "lpc-analyzer-pack-" + Path.GetRandomFileName());
            Directory.CreateDirectory(outDir);
            try
            {
                var (exit, output) = RunDotnet(
                    $"pack \"{csproj}\" -c Release -o \"{outDir}\" --nologo -v minimal", repoRoot);
                Assert.True(exit == 0, $"dotnet pack failed (exit {exit}):\n{output}");

                var nupkg = Directory.GetFiles(outDir, "Lidarr.Plugin.Analyzers*.nupkg").FirstOrDefault();
                Assert.True(nupkg is not null, $"no .nupkg produced in {outDir}:\n{output}");

                using var zip = ZipFile.OpenRead(nupkg!);
                var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

                var inAnalyzersDir = entries.Any(e =>
                    e.Equals("analyzers/dotnet/cs/" + AnalyzerDll, StringComparison.OrdinalIgnoreCase));
                var inLib = entries.Where(e =>
                    e.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                    e.EndsWith("/" + AnalyzerDll, StringComparison.OrdinalIgnoreCase)).ToList();

                Assert.True(
                    inAnalyzersDir,
                    "Analyzer DLL must be packed at analyzers/dotnet/cs/ so NuGet loads it as an analyzer. " +
                    "Package entries:\n  " + string.Join("\n  ", entries));
                Assert.True(
                    inLib.Count == 0,
                    "Analyzer DLL must NOT be under lib/ (would be treated as a runtime reference, not an " +
                    "analyzer). Offending entries:\n  " + string.Join("\n  ", inLib));
            }
            finally
            {
                try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        private static (int exitCode, string output) RunDotnet(string args, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = args,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var sb = new StringBuilder();
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(180_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, sb.ToString() + "\n[timed out after 180s]");
            }
            p.WaitForExit();
            return (p.ExitCode, sb.ToString());
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; depth < 12 && dir is not null; depth++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lidarr.plugin.common.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException(
                "Could not locate repo root (lidarr.plugin.common.sln) from " + AppContext.BaseDirectory);
        }
    }
}
