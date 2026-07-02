using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Lidarr.Plugin.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Analyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            Diagnostics.AvoidRawSend,
            Diagnostics.PreferPolicyOverload,
            Diagnostics.AvoidHtmlEncodeOnSearchTerm);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx)
    {
        if (ctx.Node is not ObjectCreationExpressionSyntax oce) return;
        var type = ctx.SemanticModel.GetTypeInfo(oce).Type as INamedTypeSymbol;
        if (type == null) return;
        if (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Net.Http.HttpClient")
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.AvoidRawSend, oce.GetLocation()));
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        if (ctx.Node is not InvocationExpressionSyntax inv) return;
        var symbol = ctx.SemanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (symbol == null) return;

        // LPC0001: Direct HttpClient.SendAsync
        if (symbol.Name == "SendAsync" && symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Net.Http.HttpClient")
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.AvoidRawSend, inv.GetLocation()));
            return;
        }

        // LPC0003: HTML-encoding / display-sanitizing a search term. Encoding ships the literal entity to
        // the API ("Beyoncé" -> "Beyonc&#233;") and loses matches — search terms must reach the wire as
        // RAW text, percent-encoded by the request builder only.
        var containingFq = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (symbol.Name == "HtmlEncode" &&
            (containingFq == "global::System.Net.WebUtility" || containingFq == "global::System.Web.HttpUtility"))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.AvoidHtmlEncodeOnSearchTerm, inv.GetLocation(), $"{symbol.ContainingType!.Name}.HtmlEncode"));
            return;
        }

        if (symbol.Name == "DisplayText" && symbol.ContainingType?.Name == "Sanitize")
        {
            ctx.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.AvoidHtmlEncodeOnSearchTerm, inv.GetLocation(), "Sanitize.DisplayText"));
            return;
        }

        // LPC0002: ExecuteWithResilienceAsync non-policy overload (maxRetries, etc.)
        if (symbol.Name == "ExecuteWithResilienceAsync" && symbol.Parameters.Length >= 3)
        {
            // Heuristic: ResiliencePolicy overload has a parameter of type ResiliencePolicy
            var hasPolicyParam = symbol.Parameters.Any(p => p.Type.Name == "ResiliencePolicy");
            if (!hasPolicyParam)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.PreferPolicyOverload, inv.GetLocation()));
            }
        }
    }
}

