using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Lidarr.Plugin.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Analyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Diagnostics.AvoidRawSend, Diagnostics.PreferPolicyOverload);

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

