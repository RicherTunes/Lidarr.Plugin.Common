using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SnippetVerifier;

internal static class Program
{
    private static int Main()
    {
        var repoRoot = LocateRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Unable to locate repository root (missing docs folder).");
            return 1;
        }

        var docsRoot = Path.Combine(repoRoot, "docs");
        var codeFencePattern = new Regex("^```(?<info>[^\\r\\n]*)$", RegexOptions.Compiled);
        var snippetSpecs = new List<SnippetSpec>();

        foreach (var docPath in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(docPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = codeFencePattern.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var info = match.Groups["info"].Value.Trim();
                if (!info.Contains("file="))
                {
                    continue;
                }

                var parts = info.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var lang = parts[0];
                var filePart = parts.FirstOrDefault(p => p.StartsWith("file=", StringComparison.OrdinalIgnoreCase));
                if (filePart is null)
                {
                    continue;
                }

                var fileSpec = filePart["file=".Length..];
                string? tag = null;
                var tagIndex = fileSpec.IndexOf('#');
                if (tagIndex >= 0)
                {
                    tag = fileSpec[(tagIndex + 1)..];
                    fileSpec = fileSpec[..tagIndex];
                }

                var absolutePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(docPath)!, fileSpec));
                snippetSpecs.Add(new SnippetSpec(docPath, i + 1, lang, absolutePath, tag));
            }
        }

        var errors = new List<string>();

        foreach (var spec in snippetSpecs)
        {
            if (!File.Exists(spec.FilePath))
            {
                errors.Add($"{Relative(repoRoot, spec.DocPath)}:{spec.LineNumber}: referenced file '{spec.FilePath}' does not exist.");
                continue;
            }

            var source = File.ReadAllText(spec.FilePath);
            var snippetText = string.IsNullOrEmpty(spec.Tag)
                ? source
                : ExtractTaggedSnippet(source, spec.Tag!);

            if (snippetText is null)
            {
                errors.Add($"{Relative(repoRoot, spec.DocPath)}:{spec.LineNumber}: unable to find snippet tag '{spec.Tag}' in '{Relative(repoRoot, spec.FilePath)}'.");
                continue;
            }

            if (ShouldCompile(snippetText))
            {
                var diagnostics = CompileSnippet(snippetText);
                if (diagnostics.Count > 0)
                {
                    foreach (var diagnostic in diagnostics)
                    {
                        errors.Add($"{Relative(repoRoot, spec.DocPath)}:{spec.LineNumber}: {diagnostic}");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        Console.WriteLine($"Snippet verification succeeded ({snippetSpecs.Count} snippet(s) checked).");
        return 0;
    }

    private static string? LocateRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(Path.Combine(directory, "docs")))
            {
                return directory;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return null;
    }

    private static string Relative(string repoRoot, string path)
        => Path.GetRelativePath(repoRoot, path);

    private static string? ExtractTaggedSnippet(string source, string tag)
    {
        var startMarker = "// snippet:" + tag;
        var endMarker = "// end-snippet";
        using var reader = new StringReader(source);
        string? line;
        bool inSnippet = false;
        var lines = new List<string>();
        while ((line = reader.ReadLine()) is not null)
        {
            if (!inSnippet)
            {
                if (line.Trim().Equals(startMarker, StringComparison.Ordinal))
                {
                    inSnippet = true;
                }
                continue;
            }

            if (line.Trim().Equals(endMarker, StringComparison.Ordinal))
            {
                return string.Join(Environment.NewLine, lines);
            }

            lines.Add(line);
        }

        return null;
    }

    private static bool ShouldCompile(string snippet)
        => snippet.Contains("class ", StringComparison.Ordinal) ||
           snippet.Contains("namespace ", StringComparison.Ordinal) ||
           snippet.Contains("struct ", StringComparison.Ordinal) ||
           snippet.Contains("interface ", StringComparison.Ordinal);

    private static IReadOnlyList<string> CompileSnippet(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet, new CSharpParseOptions(LanguageVersion.Preview));
        var references = TrustedPlatformAssemblies
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "SnippetVerifier",
            syntaxTrees: new[] { tree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        if (emitResult.Success)
        {
            return Array.Empty<string>();
        }

        return emitResult.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToArray();
    }

    private static IEnumerable<string> TrustedPlatformAssemblies
    {
        get
        {
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(tpa))
            {
                throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES not populated");
            }

            var names = new[]
            {
                "System.Runtime.dll",
                "System.Private.CoreLib.dll",
                "netstandard.dll"
            };

            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fileName = Path.GetFileName(path);
                if (names.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }
    }

    private sealed record SnippetSpec(string DocPath, int LineNumber, string Language, string FilePath, string? Tag);
}
