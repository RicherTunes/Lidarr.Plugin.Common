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

    public static readonly DiagnosticDescriptor AvoidHtmlEncodeOnSearchTerm = new(
        id: "LPC0003",
        title: "Do not HTML-encode a search term",
        messageFormat: "Do not call '{0}' on a search term; pass the RAW sanitized query (SearchQuerySanitizer) and let the request builder percent-encode it",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HTML-encoding a search term ships the literal entity to the API (\"Beyoncé\" -> \"Beyonc&#233;\") and loses matches. Search terms must reach the wire as raw text; only the request builder percent-encodes them. Use SearchQuerySanitizer + ToQueryParameterValue, never WebUtility/HttpUtility.HtmlEncode or Sanitize.DisplayText.");
}

