using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Analyzers;

/// <summary>
/// LPC0003 bans HTML-encoding / display-sanitizing a search term — the exact class of the shipped bug
/// ("Beyoncé" -> "Beyonc&amp;#233;"). The tests are hermetic: stub <c>System.Net.WebUtility</c> /
/// <c>System.Web.HttpUtility</c> / <c>Sanitize</c> types are declared in the analyzed source so the rule
/// is exercised without depending on those assemblies being present.
/// </summary>
public sealed class SearchTermHtmlEncodeAnalyzerTests
{
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Guard: the snippet must compile so the semantic model resolves symbols the analyzer keys on.
        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(compileErrors.Length == 0, "test snippet failed to compile: " + string.Join("; ", compileErrors.Select(e => e.GetMessage())));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new Lidarr.Plugin.Analyzers.Analyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private const string Stubs = @"
namespace System.Net { public static class WebUtility { public static string HtmlEncode(string s) => s; } }
namespace System.Web { public static class HttpUtility { public static string HtmlEncode(string s) => s; } }
namespace Plugin.Local { public static class Sanitize { public static string DisplayText(string s) => s; public static string Other(string s) => s; } }
";

    [Fact]
    public async Task Flags_WebUtility_HtmlEncode()
    {
        var diags = await AnalyzeAsync(Stubs + @"
namespace App { class C { string M(string q) => System.Net.WebUtility.HtmlEncode(q); } }");

        Assert.Single(diags.Where(d => d.Id == "LPC0003"));
    }

    [Fact]
    public async Task Flags_HttpUtility_HtmlEncode()
    {
        var diags = await AnalyzeAsync(Stubs + @"
namespace App { class C { string M(string q) => System.Web.HttpUtility.HtmlEncode(q); } }");

        Assert.Single(diags.Where(d => d.Id == "LPC0003"));
    }

    [Fact]
    public async Task Flags_Sanitize_DisplayText()
    {
        var diags = await AnalyzeAsync(Stubs + @"
namespace App { class C { string M(string q) => Plugin.Local.Sanitize.DisplayText(q); } }");

        var lpc0003 = diags.Where(d => d.Id == "LPC0003").ToArray();
        Assert.Single(lpc0003);
        Assert.Contains("Sanitize.DisplayText", lpc0003[0].GetMessage());
    }

    [Fact]
    public async Task DoesNotFlag_OtherSanitizeMethods_OrPlainStrings()
    {
        var diags = await AnalyzeAsync(Stubs + @"
namespace App { class C {
    string Keep(string q) => Plugin.Local.Sanitize.Other(q);
    string Raw(string q) => q.Trim();
} }");

        Assert.Empty(diags.Where(d => d.Id == "LPC0003"));
    }
}
