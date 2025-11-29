using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Security-focused compliance tests for all plugins.
/// These tests help identify common security vulnerabilities.
/// </summary>
public abstract class SecurityComplianceTestBase : IDisposable
{
    #region Abstract Properties

    /// <summary>
    /// The plugin assembly to test.
    /// </summary>
    protected abstract Assembly PluginAssembly { get; }

    /// <summary>
    /// Path to the plugin source code directory (for static analysis).
    /// </summary>
    protected abstract string? SourceCodePath { get; }

    /// <summary>
    /// Plugin name for reporting.
    /// </summary>
    protected abstract string PluginName { get; }

    #endregion

    #region Input Validation Tests

    /// <summary>
    /// Verifies the plugin validates user inputs.
    /// </summary>
    public virtual SecurityResult VerifyInputValidation()
    {
        var issues = new List<SecurityIssue>();
        var allTypes = PluginAssembly.GetTypes();

        // Check for FluentValidation usage
        var hasFluentValidation = PluginAssembly.GetReferencedAssemblies()
            .Any(a => a.Name?.Contains("FluentValidation", StringComparison.OrdinalIgnoreCase) == true);

        // Check for validator classes
        var validatorTypes = allTypes.Where(t =>
            t.Name.EndsWith("Validator", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("Validation", StringComparison.OrdinalIgnoreCase)).ToList();

        // Check settings classes for validation
        var settingsTypes = allTypes.Where(t =>
            t.Name.EndsWith("Settings", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var settings in settingsTypes)
        {
            var hasValidator = validatorTypes.Any(v =>
                v.Name.Contains(settings.Name.Replace("Settings", ""), StringComparison.OrdinalIgnoreCase));

            if (!hasValidator && !hasFluentValidation)
            {
                issues.Add(new SecurityIssue(
                    SecuritySeverity.Medium,
                    $"Settings class {settings.Name} may lack input validation",
                    "Consider implementing a FluentValidation validator"));
            }
        }

        return new SecurityResult(issues);
    }

    /// <summary>
    /// Verifies SQL injection protection (if applicable).
    /// </summary>
    public virtual SecurityResult VerifySqlInjectionProtection()
    {
        var issues = new List<SecurityIssue>();

        if (string.IsNullOrEmpty(SourceCodePath) || !Directory.Exists(SourceCodePath))
            return new SecurityResult(issues);

        var csFiles = Directory.GetFiles(SourceCodePath, "*.cs", SearchOption.AllDirectories);
        var sqlPattern = new Regex(@"(""[^""]*\+\s*\w+[^""]*""|string\.Format\([^)]*SQL|new\s+SqlCommand\([^)]*\+)",
            RegexOptions.IgnoreCase);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            if (sqlPattern.IsMatch(content))
            {
                issues.Add(new SecurityIssue(
                    SecuritySeverity.Critical,
                    $"Potential SQL injection vulnerability in {Path.GetFileName(file)}",
                    "Use parameterized queries instead of string concatenation"));
            }
        }

        return new SecurityResult(issues);
    }

    /// <summary>
    /// Verifies path traversal protection.
    /// </summary>
    public virtual SecurityResult VerifyPathTraversalProtection()
    {
        var issues = new List<SecurityIssue>();

        if (string.IsNullOrEmpty(SourceCodePath) || !Directory.Exists(SourceCodePath))
            return new SecurityResult(issues);

        var csFiles = Directory.GetFiles(SourceCodePath, "*.cs", SearchOption.AllDirectories);
        var pathPattern = new Regex(@"Path\.(Combine|Join)\([^)]*\+|File\.(Read|Write|Open)\([^)]*\+",
            RegexOptions.IgnoreCase);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);

            // Check for unsanitized path operations
            if (pathPattern.IsMatch(content))
            {
                // Check if there's path validation nearby
                if (!content.Contains("Path.GetFullPath") &&
                    !content.Contains("ValidatePath") &&
                    !content.Contains("SanitizePath"))
                {
                    issues.Add(new SecurityIssue(
                        SecuritySeverity.High,
                        $"Potential path traversal vulnerability in {Path.GetFileName(file)}",
                        "Validate and sanitize file paths before use"));
                }
            }
        }

        return new SecurityResult(issues);
    }

    #endregion

    #region Authentication & Authorization Tests

    /// <summary>
    /// Verifies secure credential handling.
    /// </summary>
    public virtual SecurityResult VerifySecureCredentialHandling()
    {
        var issues = new List<SecurityIssue>();

        if (string.IsNullOrEmpty(SourceCodePath) || !Directory.Exists(SourceCodePath))
            return new SecurityResult(issues);

        var csFiles = Directory.GetFiles(SourceCodePath, "*.cs", SearchOption.AllDirectories);
        var credentialPatterns = new[]
        {
            @"password\s*=\s*""[^""]+""",
            @"apiKey\s*=\s*""[^""]+""",
            @"secret\s*=\s*""[^""]+""",
            @"token\s*=\s*""[^""]+""",
            @"clientSecret\s*=\s*""[^""]+"""
        };

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Skip test files
            if (fileName.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var pattern in credentialPatterns)
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                var match = regex.Match(content);
                if (match.Success)
                {
                    // Check if it's a placeholder
                    var value = match.Value;
                    if (!value.Contains("{") && !value.Contains("$") && !value.Contains("<"))
                    {
                        issues.Add(new SecurityIssue(
                            SecuritySeverity.Critical,
                            $"Potential hardcoded credential in {fileName}",
                            "Use environment variables or secure configuration"));
                    }
                }
            }
        }

        return new SecurityResult(issues);
    }

    /// <summary>
    /// Verifies secure token storage.
    /// </summary>
    public virtual SecurityResult VerifySecureTokenStorage()
    {
        var issues = new List<SecurityIssue>();
        var allTypes = PluginAssembly.GetTypes();

        // Check for token storage implementations
        var tokenStorageTypes = allTypes.Where(t =>
            t.Name.Contains("TokenStore", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("TokenStorage", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("CredentialStore", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var storageType in tokenStorageTypes)
        {
            var methods = storageType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.Instance | BindingFlags.Static);

            var hasEncryption = methods.Any(m =>
                m.Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Protect", StringComparison.OrdinalIgnoreCase));

            var usesDataProtection = storageType.GetInterfaces()
                .Any(i => i.Name.Contains("DataProtect", StringComparison.OrdinalIgnoreCase));

            if (!hasEncryption && !usesDataProtection)
            {
                issues.Add(new SecurityIssue(
                    SecuritySeverity.High,
                    $"Token storage {storageType.Name} may store tokens in plaintext",
                    "Use DataProtection API or encrypt tokens before storage"));
            }
        }

        return new SecurityResult(issues);
    }

    #endregion

    #region Network Security Tests

    /// <summary>
    /// Verifies HTTPS is enforced for sensitive communications.
    /// </summary>
    public virtual SecurityResult VerifyHttpsEnforcement()
    {
        var issues = new List<SecurityIssue>();

        if (string.IsNullOrEmpty(SourceCodePath) || !Directory.Exists(SourceCodePath))
            return new SecurityResult(issues);

        var csFiles = Directory.GetFiles(SourceCodePath, "*.cs", SearchOption.AllDirectories);
        var httpPattern = new Regex(@"""http://[^""]*""", RegexOptions.IgnoreCase);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            // Skip test files and localhost
            if (fileName.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            var matches = httpPattern.Matches(content);
            foreach (Match match in matches)
            {
                var url = match.Value;
                if (!url.Contains("localhost") && !url.Contains("127.0.0.1"))
                {
                    issues.Add(new SecurityIssue(
                        SecuritySeverity.Medium,
                        $"Non-HTTPS URL found in {fileName}: {url}",
                        "Use HTTPS for all external communications"));
                }
            }
        }

        return new SecurityResult(issues);
    }

    /// <summary>
    /// Verifies certificate validation is not disabled.
    /// </summary>
    public virtual SecurityResult VerifyCertificateValidation()
    {
        var issues = new List<SecurityIssue>();

        if (string.IsNullOrEmpty(SourceCodePath) || !Directory.Exists(SourceCodePath))
            return new SecurityResult(issues);

        var csFiles = Directory.GetFiles(SourceCodePath, "*.cs", SearchOption.AllDirectories);
        var unsafePatterns = new[]
        {
            "ServerCertificateValidationCallback",
            "ServerCertificateCustomValidationCallback",
            "RemoteCertificateValidationCallback",
            "return true; // Certificate validation"
        };

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (var pattern in unsafePatterns)
            {
                if (content.Contains(pattern))
                {
                    // Check if it's returning true (bypassing validation)
                    if (content.Contains("=> true") || content.Contains("return true"))
                    {
                        issues.Add(new SecurityIssue(
                            SecuritySeverity.Critical,
                            $"Certificate validation may be disabled in {fileName}",
                            "Do not disable certificate validation in production"));
                    }
                }
            }
        }

        return new SecurityResult(issues);
    }

    #endregion

    #region Logging Security Tests

    /// <summary>
    /// Verifies sensitive data is not logged.
    /// </summary>
    public virtual SecurityResult VerifyNoSensitiveDataLogging()
    {
        var issues = new List<SecurityIssue>();

        if (string.IsNullOrEmpty(SourceCodePath) || !Directory.Exists(SourceCodePath))
            return new SecurityResult(issues);

        var csFiles = Directory.GetFiles(SourceCodePath, "*.cs", SearchOption.AllDirectories);
        var logPatterns = new[]
        {
            @"\.Log.*password",
            @"\.Log.*apiKey",
            @"\.Log.*secret",
            @"\.Log.*token",
            @"\.Log.*credential",
            @"Console\.Write.*password",
            @"Debug\.Write.*token"
        };

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (var pattern in logPatterns)
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                if (regex.IsMatch(content))
                {
                    issues.Add(new SecurityIssue(
                        SecuritySeverity.High,
                        $"Potential sensitive data logging in {fileName}",
                        "Avoid logging credentials, tokens, or API keys"));
                }
            }
        }

        return new SecurityResult(issues);
    }

    #endregion

    #region Dependency Security Tests

    /// <summary>
    /// Checks for known vulnerable dependencies.
    /// </summary>
    public virtual SecurityResult VerifyNoDependencyVulnerabilities()
    {
        var issues = new List<SecurityIssue>();
        var references = PluginAssembly.GetReferencedAssemblies();

        // Known vulnerable versions (simplified check)
        var vulnerableVersions = new Dictionary<string, Version>
        {
            ["Newtonsoft.Json"] = new Version(12, 0, 0), // Pre-12 had vulnerabilities
            ["System.Text.Json"] = new Version(6, 0, 0)
        };

        foreach (var reference in references)
        {
            if (reference.Name != null &&
                vulnerableVersions.TryGetValue(reference.Name, out var minSafeVersion))
            {
                if (reference.Version != null && reference.Version < minSafeVersion)
                {
                    issues.Add(new SecurityIssue(
                        SecuritySeverity.High,
                        $"Potentially vulnerable dependency: {reference.Name} v{reference.Version}",
                        $"Update to version {minSafeVersion} or later"));
                }
            }
        }

        return new SecurityResult(issues);
    }

    #endregion

    /// <summary>
    /// Runs all security compliance checks.
    /// </summary>
    public virtual SecurityReport RunAllSecurityChecks()
    {
        var results = new Dictionary<string, SecurityResult>
        {
            ["InputValidation"] = VerifyInputValidation(),
            ["SqlInjectionProtection"] = VerifySqlInjectionProtection(),
            ["PathTraversalProtection"] = VerifyPathTraversalProtection(),
            ["SecureCredentialHandling"] = VerifySecureCredentialHandling(),
            ["SecureTokenStorage"] = VerifySecureTokenStorage(),
            ["HttpsEnforcement"] = VerifyHttpsEnforcement(),
            ["CertificateValidation"] = VerifyCertificateValidation(),
            ["NoSensitiveDataLogging"] = VerifyNoSensitiveDataLogging(),
            ["NoDependencyVulnerabilities"] = VerifyNoDependencyVulnerabilities()
        };

        return new SecurityReport(PluginName, results);
    }

    public virtual void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Severity levels for security issues.
/// </summary>
public enum SecuritySeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Represents a single security issue.
/// </summary>
public record SecurityIssue(
    SecuritySeverity Severity,
    string Description,
    string Recommendation);

/// <summary>
/// Result of a single security check.
/// </summary>
public record SecurityResult(IReadOnlyList<SecurityIssue> Issues)
{
    public bool Passed => !Issues.Any(i => i.Severity >= SecuritySeverity.High);
    public int CriticalCount => Issues.Count(i => i.Severity == SecuritySeverity.Critical);
    public int HighCount => Issues.Count(i => i.Severity == SecuritySeverity.High);
}

/// <summary>
/// Complete security report for a plugin.
/// </summary>
public record SecurityReport(
    string PluginName,
    IReadOnlyDictionary<string, SecurityResult> Results)
{
    public bool Passed => Results.Values.All(r => r.Passed);

    public int TotalCritical => Results.Values.Sum(r => r.CriticalCount);
    public int TotalHigh => Results.Values.Sum(r => r.HighCount);

    public IEnumerable<SecurityIssue> GetAllIssues() =>
        Results.Values.SelectMany(r => r.Issues);

    public IEnumerable<SecurityIssue> GetCriticalIssues() =>
        GetAllIssues().Where(i => i.Severity == SecuritySeverity.Critical);
}
