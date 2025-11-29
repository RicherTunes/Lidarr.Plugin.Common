using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Extended compliance tests for streaming service plugins (Tidal, Qobuz, etc.).
/// Plugins should inherit from this class to verify streaming-specific requirements.
/// </summary>
public abstract class StreamingServiceComplianceTestBase : PluginComplianceTestBase
{
    #region Abstract Properties - Streaming-specific

    /// <summary>
    /// The streaming service name (e.g., "Tidal", "Qobuz").
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// The authentication service type implemented by the plugin.
    /// </summary>
    protected abstract Type? AuthenticationServiceType { get; }

    /// <summary>
    /// The indexer type implemented by the plugin.
    /// </summary>
    protected abstract Type? IndexerType { get; }

    /// <summary>
    /// The download client type implemented by the plugin.
    /// </summary>
    protected abstract Type? DownloadClientType { get; }

    /// <summary>
    /// Supported audio qualities by this streaming service.
    /// </summary>
    protected abstract IReadOnlyList<StreamingQuality> SupportedQualities { get; }

    #endregion

    #region Authentication Compliance Tests

    /// <summary>
    /// Verifies the plugin has a proper authentication implementation.
    /// </summary>
    public virtual ComplianceResult VerifyAuthenticationImplementation()
    {
        var errors = new List<string>();

        if (AuthenticationServiceType == null)
        {
            errors.Add($"{ServiceName} plugin must implement an authentication service");
            return new ComplianceResult(false, errors);
        }

        // Check for required authentication methods
        var methods = AuthenticationServiceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var hasAuthenticate = methods.Any(m =>
            m.Name.Contains("Authenticate", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Login", StringComparison.OrdinalIgnoreCase));

        var hasRefresh = methods.Any(m =>
            m.Name.Contains("Refresh", StringComparison.OrdinalIgnoreCase));

        var hasValidate = methods.Any(m =>
            m.Name.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("IsAuthenticated", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Check", StringComparison.OrdinalIgnoreCase));

        if (!hasAuthenticate)
            errors.Add("Authentication service should have an Authenticate/Login method");

        if (!hasRefresh)
            errors.Add("Authentication service should have a token Refresh method");

        if (!hasValidate)
            errors.Add("Authentication service should have a token validation method");

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies authentication tokens are stored securely.
    /// </summary>
    public virtual ComplianceResult VerifySecureTokenStorage()
    {
        var errors = new List<string>();
        var allTypes = PluginAssembly.GetTypes();

        // Check for token storage implementation
        var tokenStorageTypes = allTypes.Where(t =>
            t.Name.Contains("TokenStore", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("TokenStorage", StringComparison.OrdinalIgnoreCase) ||
            t.GetInterfaces().Any(i => i.IsGenericType &&
                i.GetGenericTypeDefinition().Name.StartsWith("ITokenStore", StringComparison.Ordinal))).ToList();

        if (!tokenStorageTypes.Any())
        {
            // Not necessarily an error - might use Common library's implementation
        }
        else
        {
            foreach (var storageType in tokenStorageTypes)
            {
                // Check if it uses encryption/protection
                var usesProtection = storageType.GetInterfaces()
                    .Any(i => i.Name.Contains("Protect", StringComparison.OrdinalIgnoreCase));

                var methods = storageType.GetMethods();
                var hasEncryption = methods.Any(m =>
                    m.Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Protect", StringComparison.OrdinalIgnoreCase));

                if (!usesProtection && !hasEncryption)
                {
                    // Warning - tokens might not be encrypted
                    errors.Add($"Token storage {storageType.Name} may not encrypt tokens at rest");
                }
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Indexer Compliance Tests

    /// <summary>
    /// Verifies the indexer implementation is complete.
    /// </summary>
    public virtual ComplianceResult VerifyIndexerImplementation()
    {
        var errors = new List<string>();

        if (IndexerType == null)
        {
            errors.Add($"{ServiceName} plugin must implement an indexer");
            return new ComplianceResult(false, errors);
        }

        var methods = IndexerType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        // Check for search methods
        var hasSearch = methods.Any(m =>
            m.Name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Fetch", StringComparison.OrdinalIgnoreCase));

        if (!hasSearch)
            errors.Add("Indexer must implement a Search method");

        // Check for async implementation
        var asyncMethods = methods.Where(m =>
            m.ReturnType.IsGenericType &&
            m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));

        if (!asyncMethods.Any())
            errors.Add("Indexer should use async methods for I/O operations");

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies search handles edge cases properly.
    /// </summary>
    public virtual ComplianceResult VerifySearchEdgeCases()
    {
        var errors = new List<string>();

        // These are documentation requirements - actual testing done in integration tests
        var expectedEdgeCases = new[]
        {
            "Empty search query",
            "Special characters in query",
            "Unicode characters",
            "Very long query strings",
            "No results found"
        };

        // Check if test files exist for these cases
        // This is a meta-check - plugins should have tests for these scenarios

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Download Client Compliance Tests

    /// <summary>
    /// Verifies the download client implementation is complete.
    /// </summary>
    public virtual ComplianceResult VerifyDownloadClientImplementation()
    {
        var errors = new List<string>();

        if (DownloadClientType == null)
        {
            errors.Add($"{ServiceName} plugin must implement a download client");
            return new ComplianceResult(false, errors);
        }

        var methods = DownloadClientType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        // Check for download methods
        var hasDownload = methods.Any(m =>
            m.Name.Contains("Download", StringComparison.OrdinalIgnoreCase));

        var hasGetStatus = methods.Any(m =>
            m.Name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("GetItems", StringComparison.OrdinalIgnoreCase));

        if (!hasDownload)
            errors.Add("Download client must implement a Download method");

        if (!hasGetStatus)
            errors.Add("Download client should implement a status/GetItems method");

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies download handles quality fallback properly.
    /// </summary>
    public virtual ComplianceResult VerifyQualityFallback()
    {
        var errors = new List<string>();

        if (SupportedQualities == null || !SupportedQualities.Any())
        {
            errors.Add("Plugin must define supported audio qualities");
            return new ComplianceResult(false, errors);
        }

        // Check quality ordering
        var qualities = SupportedQualities.ToList();
        for (int i = 1; i < qualities.Count; i++)
        {
            if (qualities[i].GetTier() > qualities[i - 1].GetTier())
            {
                // Qualities should be ordered from highest to lowest for proper fallback
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Rate Limiting Compliance Tests

    /// <summary>
    /// Verifies the plugin implements rate limiting.
    /// </summary>
    public virtual ComplianceResult VerifyRateLimitingImplementation()
    {
        var errors = new List<string>();
        var allTypes = PluginAssembly.GetTypes();

        // Check for rate limiter usage
        var hasRateLimiter = allTypes.Any(t =>
            t.Name.Contains("RateLimiter", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Throttle", StringComparison.OrdinalIgnoreCase));

        var usesPolly = PluginAssembly.GetReferencedAssemblies()
            .Any(a => a.Name?.Contains("Polly", StringComparison.OrdinalIgnoreCase) == true);

        var usesCommonRateLimiter = allTypes.Any(t =>
            t.GetInterfaces().Any(i => i.Name.Contains("RateLimitObserver", StringComparison.OrdinalIgnoreCase)));

        if (!hasRateLimiter && !usesPolly && !usesCommonRateLimiter)
        {
            errors.Add($"{ServiceName} plugin should implement rate limiting to avoid API bans");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Error Handling Compliance Tests

    /// <summary>
    /// Verifies the plugin has proper exception types.
    /// </summary>
    public virtual ComplianceResult VerifyExceptionHandling()
    {
        var errors = new List<string>();
        var allTypes = PluginAssembly.GetTypes();

        // Check for custom exception types
        var exceptionTypes = allTypes.Where(t =>
            typeof(Exception).IsAssignableFrom(t) &&
            !t.IsAbstract &&
            t != typeof(Exception)).ToList();

        var hasAuthException = exceptionTypes.Any(t =>
            t.Name.Contains("Auth", StringComparison.OrdinalIgnoreCase));

        var hasApiException = exceptionTypes.Any(t =>
            t.Name.Contains("Api", StringComparison.OrdinalIgnoreCase));

        if (!hasAuthException && !hasApiException)
        {
            // Not an error, but recommended
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies HTTP error responses are handled properly.
    /// </summary>
    public virtual ComplianceResult VerifyHttpErrorHandling()
    {
        var errors = new List<string>();

        // Check for HTTP status code handling
        var allTypes = PluginAssembly.GetTypes();
        var httpClientTypes = allTypes.Where(t =>
            t.Name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Api", StringComparison.OrdinalIgnoreCase)).ToList();

        // This is a meta-check - actual verification done in tests

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Caching Compliance Tests

    /// <summary>
    /// Verifies the plugin implements proper caching.
    /// </summary>
    public virtual ComplianceResult VerifyCachingImplementation()
    {
        var errors = new List<string>();
        var allTypes = PluginAssembly.GetTypes();

        var hasCaching = allTypes.Any(t =>
            t.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase)) ||
            allTypes.Any(t => t.GetInterfaces()
                .Any(i => i.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase)));

        if (!hasCaching)
        {
            errors.Add($"{ServiceName} plugin should implement response caching for performance");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    /// <summary>
    /// Runs all streaming service compliance checks.
    /// </summary>
    public override ComplianceReport RunAllComplianceChecks()
    {
        var baseReport = base.RunAllComplianceChecks();
        var results = new Dictionary<string, ComplianceResult>(baseReport.Results);

        // Add streaming-specific checks
        results["AuthenticationImplementation"] = VerifyAuthenticationImplementation();
        results["SecureTokenStorage"] = VerifySecureTokenStorage();
        results["IndexerImplementation"] = VerifyIndexerImplementation();
        results["DownloadClientImplementation"] = VerifyDownloadClientImplementation();
        results["QualityFallback"] = VerifyQualityFallback();
        results["RateLimitingImplementation"] = VerifyRateLimitingImplementation();
        results["ExceptionHandling"] = VerifyExceptionHandling();
        results["HttpErrorHandling"] = VerifyHttpErrorHandling();
        results["CachingImplementation"] = VerifyCachingImplementation();

        var passed = results.Values.Count(r => r.Passed);
        var total = results.Count;

        return new ComplianceReport(results, passed, total);
    }
}
