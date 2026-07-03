using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hygiene
{
    /// <summary>
    /// Source-hygiene guards over the whole repository tree.
    ///
    /// Origin: an external audit found a raw NUL byte (0x00) embedded inside a string
    /// literal in <c>tests/Services/Intelligence/SearchQuerySanitizerTests.cs</c> — a tool
    /// had reported "binary file matches" on a <c>.cs</c> file and that anomaly was worked
    /// around instead of treated as a finding. This guard makes that finding-class fail by
    /// default: no tracked text source may contain a NUL byte (use the C# <c>\0</c> escape
    /// when a NUL character is genuinely needed in a literal).
    /// </summary>
    public class SourceHygieneTests
    {
        private static readonly string[] TextExtensions =
        {
            ".cs", ".csproj", ".props", ".targets", ".sln",
            ".md", ".json", ".yml", ".yaml", ".txt",
            ".sh", ".ps1", ".editorconfig"
        };

        // Directories that hold generated output or the vendored submodule — not our sources.
        private static readonly string[] ExcludedDirSegments =
        {
            "/bin/", "/obj/", "/.git/", "/ext/", "/artifacts/", "/node_modules/",
            "/TestResults/", "/.vs/"
        };

        [Fact]
        public void No_tracked_text_source_contains_a_NUL_byte()
        {
            var repoRoot = FindRepoRoot();
            var offenders = new List<string>();

            foreach (var file in EnumerateTextSources(repoRoot))
            {
                var bytes = File.ReadAllBytes(file);
                var idx = Array.IndexOf(bytes, (byte)0);
                if (idx >= 0)
                {
                    var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    offenders.Add($"{rel} (first NUL at byte offset {idx})");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "Found NUL byte(s) in tracked text source(s). Replace a literal NUL with the " +
                "C# '\\0' escape; otherwise the file is corrupt:\n  " +
                string.Join("\n  ", offenders));
        }

        [Fact]
        public void Adaptive_rate_limiting_handler_uses_shared_rate_limit_header_utilities()
        {
            var repoRoot = FindRepoRoot();
            var path = Path.Combine(repoRoot, "src", "Services", "Http", "AdaptiveRateLimitingHandler.cs");
            var source = File.ReadAllText(path);

            Assert.Contains("RateLimitHeaderUtilities.BuildHostFirstSegmentKey", source);
            Assert.Contains("RateLimitHeaderUtilities.ResolveRetryAfter", source);
            Assert.DoesNotContain("private static string BuildEndpointKey", source);
            Assert.DoesNotContain("private static TimeSpan ResolveRetryAfter", source);
        }

        private static IEnumerable<string> EnumerateTextSources(string repoRoot)
        {
            foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
            {
                var normalized = "/" + Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (ExcludedDirSegments.Any(seg => normalized.Contains(seg, StringComparison.Ordinal)))
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                if (TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
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
