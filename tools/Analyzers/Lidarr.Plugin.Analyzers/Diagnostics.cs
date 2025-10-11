using Microsoft.CodeAnalysis;

namespace Lidarr.Plugin.Analyzers;

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor AvoidRawSend = new(
        id: "LPC0001",
        title: "Avoid raw HttpClient.SendAsync",
        messageFormat: "Call HttpClientExtensions.ExecuteWithResilienceAsync / SendWithResilienceAsync instead of raw SendAsync",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Use the policy-first executor and Options stamping to ensure retries, gates, and canonical caching.");

    public static readonly DiagnosticDescriptor PreferPolicyOverload = new(
        id: "LPC0002",
        title: "Prefer policy-based overload of ExecuteWithResilienceAsync",
        messageFormat: "Use the ResiliencePolicy overload or SendWithResilienceAsync (builder) to avoid parameter mistakes",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Prefer the overload that accepts ResiliencePolicy or the builder path.");
}

