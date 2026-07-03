using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Abstractions.Results;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Services.Download;
using Lidarr.Plugin.Common.Services.Registration;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Unit tests for the behavior-contract checks added to <see cref="EcosystemParityTestBase"/>.
/// Tests use a configurable harness subclass that lets each test inject its own type set
/// and source root, so assertions don't depend on real plugin assemblies.
/// </summary>
public class EcosystemParityTestBaseExtensionTests : IDisposable
{
    private readonly string _tempRepo;

    public EcosystemParityTestBaseExtensionTests()
    {
        _tempRepo = Path.Combine(Path.GetTempPath(), "parity-ext-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRepo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRepo, recursive: true); } catch { /* best-effort */ }
    }

    // --- Test harness ---

    private sealed class Harness : EcosystemParityTestBase
    {
        public Harness(string repoRoot)
        {
            RepoRootValue = repoRoot;
        }

        public string RepoRootValue { get; }
        public Assembly? AssemblyValue { get; set; }
        public IEnumerable<Type>? TypesValue { get; set; }
        public string? SourceRootValue { get; set; }

        protected override string RepoRootPath => RepoRootValue;
        protected override string PluginId => "harness";
        protected override string PluginJsonRelativePath => "plugin.json";
        protected override Assembly? PluginAssembly => AssemblyValue;
        protected override string PluginSourceRoot => SourceRootValue ?? base.PluginSourceRoot;
        protected override IEnumerable<Type> SafeGetTypes(Assembly assembly)
            => TypesValue ?? base.SafeGetTypes(assembly);

        // Expose protected check methods for tests.
        public ComplianceResult RunFileTokenStore() => Check_UsesCommonFileTokenStore();
        public ComplianceResult RunHttpResponseCache() => Check_UsesCommonHttpResponseCache();
        public ComplianceResult RunBridgeDefaults() => Check_RegistersBridgeDefaults();
        public ComplianceResult RunCapabilities() => Check_PluginManifest_Capabilities_HaveBackingTypes();
        public ComplianceResult RunValidationDrift() => Check_NoFluentValidation_ErrorsApi_Drift();
        public ComplianceResult RunConfigRoots() => Check_UsesCommonPluginConfigRoots();
        public ComplianceResult RunLyricsEnricher() => Check_UsesCommonLyricsEnricher();
        public ComplianceResult RunDiagnosticTypes() => Check_UsesCommonDiagnosticTypes();
        public ComplianceResult RunDownloadTelemetrySink() => Check_UsesCommonDownloadTelemetrySink();
        public ComplianceResult RunPathTraversalGuard() => Check_DownloadClientUsesPathTraversalGuard();
        public ComplianceResult RunFileClassNameParity() => Check_FileClassNameParity();
        public ComplianceResult RunClaudeMdHelpers() => Check_ClaudeMdDocumentsCommonHelpers();
        public ComplianceResult RunDownloadClientIdStamp() => Check_DownloadClientStampsRegisteredClientId();
        public ComplianceResult RunPayloadValidator() => Check_DownloadClientUsesCommonPayloadValidator();
        public ComplianceResult RunCoverArtEmbeddingComplianceAdoption() => Check_SimpleDownloadOrchestratorCoverArtComplianceAdopted();
    }

    /// <summary>A plugin-local re-declaration of the lyrics enricher (forbidden — must use common's).</summary>
    private static class RogueLyrics
    {
        public interface ILyricsEnricher { }
    }

    /// <summary>A fake plugin *HealthDiagnostics with a nested DiagnosticTypes (forbidden duplicate).</summary>
    private static class FakeHealthDiagnostics
    {
        public static class DiagnosticTypes { public const string AuthValidate = "auth_validate"; }
    }

    /// <summary>A fake plugin-local download client (implements the host-bridge IDownloadClient contract).</summary>
    private sealed class FakeDownloadClient : IDownloadClient
    {
        public System.Threading.Tasks.ValueTask<PluginValidationResult> InitializeAsync(System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.ValueTask<string> EnqueueAlbumDownloadAsync(string albumId, string outputPath, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.ValueTask<bool> RemoveDownloadAsync(string downloadId, bool deleteData = false, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.ValueTask<IReadOnlyList<StreamingDownloadItem>> GetActiveDownloadsAsync(System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.ValueTask<StreamingDownloadItem?> GetDownloadAsync(string downloadId, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public System.Threading.Tasks.ValueTask DisposeAsync() => default;
    }

    // --- Fixture types ---

    /// <summary>A fake plugin-local ITokenStore impl (forbidden).</summary>
    private sealed class RogueTokenStore : ITokenStore<string>
    {
        public System.Threading.Tasks.Task<TokenEnvelope<string>?> LoadAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult<TokenEnvelope<string>?>(null);
        public System.Threading.Tasks.Task SaveAsync(TokenEnvelope<string> envelope, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task ClearAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>A fake plugin-local IStreamingResponseCache (forbidden).</summary>
    private sealed class RogueResponseCache : IStreamingResponseCache
    {
        public T? Get<T>(string endpoint, Dictionary<string, string> parameters) where T : class => null;
        public void Set<T>(string endpoint, Dictionary<string, string> parameters, T value) where T : class { }
        public void Set<T>(string endpoint, Dictionary<string, string> parameters, T value, TimeSpan duration) where T : class { }
        public bool ShouldCache(string endpoint) => false;
        public TimeSpan GetCacheDuration(string endpoint) => TimeSpan.Zero;
        public string GenerateCacheKey(string endpoint, Dictionary<string, string> parameters) => endpoint;
        public void Clear() { }
        public void ClearEndpoint(string endpoint) { }
    }

    /// <summary>Plugin-local config-path helper (forbidden).</summary>
    private sealed class MyServiceConfigPathDefaults { }

    /// <summary>A fake plugin-local IDownloadTelemetrySink (forbidden — hand-rolls the log format).</summary>
    private sealed class RogueTelemetrySink : IDownloadTelemetrySink
    {
        public void OnTrackCompleted(DownloadTelemetry telemetry) { }
    }

    /// <summary>Subclass of StreamingPluginModule for bridge-defaults check.</summary>
    private abstract class FakeModule : StreamingPluginModule { }

    /// <summary>
    /// A legitimate plugin-local subclass of common's <see cref="StreamingResponseCache"/>
    /// — supported extension pattern (custom keys/durations).
    /// </summary>
    private sealed class LegitimateResponseCacheSubclass : StreamingResponseCache
    {
        public LegitimateResponseCacheSubclass() : base(logger: null!, policyProvider: null) { }
        protected override string GetServiceName() => "Test";
    }

    /// <summary>
    /// A legitimate plugin-local fail-fast token store, marked with the opt-in attribute.
    /// </summary>
    [ParityAllowedTokenStore("fail-fast no-op for tests; mirrors tidalarr's FailOnIOTokenStore")]
    private sealed class AllowedFailFastTokenStore : ITokenStore<string>
    {
        public System.Threading.Tasks.Task<TokenEnvelope<string>?> LoadAsync(System.Threading.CancellationToken ct = default) => throw new InvalidOperationException();
        public System.Threading.Tasks.Task SaveAsync(TokenEnvelope<string> envelope, System.Threading.CancellationToken ct = default) => throw new InvalidOperationException();
        public System.Threading.Tasks.Task ClearAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
    }

    // --- Check_UsesCommonFileTokenStore ---

    [Fact]
    public void TokenStore_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunFileTokenStore().Passed);
    }

    [Fact]
    public void TokenStore_PluginLocalImpl_Fails()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(RogueTokenStore) },
        };
        var r = h.RunFileTokenStore();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("RogueTokenStore"));
    }

    [Fact]
    public void TokenStore_CommonSubclass_Passes()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunFileTokenStore().Passed);
    }

    [Fact]
    public void TokenStore_AllowedAttribute_Passes()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(AllowedFailFastTokenStore) },
        };
        Assert.True(h.RunFileTokenStore().Passed);
    }

    [Fact]
    public void TokenStore_CommonNamespaceInternalized_Passes()
    {
        // Type lives under Lidarr.Plugin.Common.* but in the tests assembly — simulating
        // ILRepack-internalized common types.
        var t = Type.GetType("Lidarr.Plugin.Common.Internalized.TokenStores.FakeInternalizedTokenStore");
        Assert.NotNull(t);
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { t! },
        };
        Assert.True(h.RunFileTokenStore().Passed);
    }

    // --- Check_UsesCommonHttpResponseCache ---

    [Fact]
    public void ResponseCache_PluginLocalImpl_Fails()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(RogueResponseCache) },
        };
        var r = h.RunHttpResponseCache();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("RogueResponseCache"));
    }

    [Fact]
    public void ResponseCache_NoImpl_Passes()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunHttpResponseCache().Passed);
    }

    [Fact]
    public void ResponseCache_LegitimateCommonSubclass_Passes()
    {
        // Subclassing common's StreamingResponseCache is the supported extension pattern
        // (qobuzarr / tidalarr endpoint-specific cache key/duration).
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(LegitimateResponseCacheSubclass) },
        };
        Assert.True(h.RunHttpResponseCache().Passed);
    }

    [Fact]
    public void ResponseCache_CommonNamespaceInternalized_Passes()
    {
        var t = Type.GetType("Lidarr.Plugin.Common.Internalized.TokenStores.FakeInternalizedResponseCache");
        Assert.NotNull(t);
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { t! },
        };
        Assert.True(h.RunHttpResponseCache().Passed);
    }

    // --- Check_RegistersBridgeDefaults ---

    [Fact]
    public void BridgeDefaults_NoModule_NotApplicable_Passes()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunBridgeDefaults().Passed);
    }

    [Fact]
    public void BridgeDefaults_TestsAssemblyContainsString_Passes()
    {
        // The tests assembly references and uses AddBridgeDefaults via other tests, and at
        // minimum the literal string "AddBridgeDefaults" appears in this very test file's
        // metadata — when a FakeModule subclass is detected we expect Pass.
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeModule) },
        };
        Assert.True(h.RunBridgeDefaults().Passed);
    }

    // --- Check_PluginManifest_Capabilities_HaveBackingTypes ---

    [Fact]
    public void Capabilities_NoPluginJson_Skipped()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = Array.Empty<Type>(),
        };
        Assert.True(h.RunCapabilities().Passed);
    }

    [Fact]
    public void Capabilities_DeclaresProvidesIndexer_NoBackingType_Fails()
    {
        File.WriteAllText(Path.Combine(_tempRepo, "plugin.json"),
            "{\"capabilities\":[\"ProvidesIndexer\"]}");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = Array.Empty<Type>(),
        };
        var r = h.RunCapabilities();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("ProvidesIndexer"));
    }

    [Fact]
    public void Capabilities_NoCapabilitiesField_Passes()
    {
        File.WriteAllText(Path.Combine(_tempRepo, "plugin.json"), "{\"id\":\"x\"}");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = Array.Empty<Type>(),
        };
        Assert.True(h.RunCapabilities().Passed);
    }

    // --- Check_NoFluentValidation_ErrorsApi_Drift ---

    [Fact]
    public void ValidationDrift_NoSourceFiles_Passes()
    {
        var srcDir = Path.Combine(_tempRepo, "empty-src");
        Directory.CreateDirectory(srcDir);
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        Assert.True(h.RunValidationDrift().Passed);
    }

    [Fact]
    public void ValidationDrift_DetectsLinqChainOnErrors_Fails()
    {
        // Refined logic: only LINQ chaining off `.Errors` is flagged (the unstable getter
        // pattern). Bare `var e = r.Errors;` and list-mutation calls are tolerated.
        var srcDir = Path.Combine(_tempRepo, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Bad.cs"),
            "using System.Linq; using FluentValidation; class X { void M(FluentValidation.Results.ValidationResult r) { var n = r.Errors.Count(); } }");
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        var rs = h.RunValidationDrift();
        Assert.False(rs.Passed);
    }

    [Fact]
    public void ValidationDrift_BareErrorsAssignment_Passes()
    {
        // Refined logic deliberately tolerates bare `.Errors` assignment — only LINQ
        // chains depend on the drifted getter shape. Plugins that need stricter analysis
        // can override the check.
        var srcDir = Path.Combine(_tempRepo, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Bare.cs"),
            "using FluentValidation; class X { void M(FluentValidation.Results.ValidationResult r) { var e = r.Errors; } }");
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        Assert.True(h.RunValidationDrift().Passed);
    }

    [Fact]
    public void ValidationDrift_ListMutationAddIsAllowed_Passes()
    {
        // .Errors.Add(...) works on either IList<T> or List<T>, so it's stable across
        // FV 9.x ↔ 11.x. Only LINQ chaining off the getter is unstable.
        var srcDir = Path.Combine(_tempRepo, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "ListMutation.cs"),
            "using FluentValidation; using FluentValidation.Results; class M { void Run(ValidationResult r) { r.Errors.Add(new ValidationFailure(\"p\", \"m\")); } }");
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        Assert.True(h.RunValidationDrift().Passed);
    }

    [Fact]
    public void ValidationDrift_LinqSelectFlagged_Fails()
    {
        var srcDir = Path.Combine(_tempRepo, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Linq.cs"),
            "using System.Linq; using FluentValidation; using FluentValidation.Results; class M { string Run(ValidationResult r) => string.Join(\",\", r.Errors.Select(e => e.ErrorMessage)); }");
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        Assert.False(h.RunValidationDrift().Passed);
    }

    [Fact]
    public void ValidationDrift_LinqAnyFlagged_Fails()
    {
        var srcDir = Path.Combine(_tempRepo, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "LinqAny.cs"),
            "using System.Linq; using FluentValidation; using FluentValidation.Results; class M { bool Run(ValidationResult r) => r.Errors.Any(); }");
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        Assert.False(h.RunValidationDrift().Passed);
    }

    [Fact]
    public void ValidationDrift_NoFluentValidationReference_Passes()
    {
        var srcDir = Path.Combine(_tempRepo, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Clean.cs"),
            "class Y { void M() { var x = 1; } }");
        var h = new Harness(_tempRepo) { SourceRootValue = srcDir };
        Assert.True(h.RunValidationDrift().Passed);
    }

    // --- Check_UsesCommonPluginConfigRoots ---

    [Fact]
    public void ConfigRoots_PluginLocalDefaults_Fails()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(MyServiceConfigPathDefaults) },
        };
        var r = h.RunConfigRoots();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("MyServiceConfigPathDefaults"));
    }

    [Fact]
    public void ConfigRoots_NoOffenders_Passes()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunConfigRoots().Passed);
    }

    // --- Check_UsesCommonLyricsEnricher ---

    [Fact]
    public void LyricsEnricher_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunLyricsEnricher().Passed);
    }

    [Fact]
    public void LyricsEnricher_PluginLocalRedeclaration_Fails()
    {
        // A plugin-local type named ILyricsEnricher/LyricsEnricher (the historical qobuz/tidal
        // duplication) must be flagged — lyrics is consolidated in common.
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(RogueLyrics.ILyricsEnricher) },
        };
        var r = h.RunLyricsEnricher();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("ILyricsEnricher"));
    }

    [Fact]
    public void LyricsEnricher_CommonType_Passes()
    {
        // Common's own enricher (here referenced directly; in production it's ILRepack-internalized
        // but keeps its Lidarr.Plugin.Common.* namespace) is the canonical implementation.
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(Lidarr.Plugin.Common.Services.Lyrics.LyricsEnricher) },
        };
        Assert.True(h.RunLyricsEnricher().Passed);
    }

    // --- Check_UsesCommonDiagnosticTypes ---

    [Fact]
    public void DiagnosticTypes_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunDiagnosticTypes().Passed);
    }

    [Fact]
    public void DiagnosticTypes_PluginLocalNestedInHealthDiagnostics_Fails()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeHealthDiagnostics.DiagnosticTypes) },
        };
        var r = h.RunDiagnosticTypes();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("DiagnosticTypes"));
    }

    [Fact]
    public void DiagnosticTypes_CommonType_Passes()
    {
        // common's canonical DiagnosticTypes is top-level in Abstractions.Diagnostics (no
        // *HealthDiagnostics declaring type) so it is not flagged.
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(Lidarr.Plugin.Common.Abstractions.Diagnostics.DiagnosticTypes) },
        };
        Assert.True(h.RunDiagnosticTypes().Passed);
    }

    // --- Check_UsesCommonDownloadTelemetrySink ---

    [Fact]
    public void TelemetrySink_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunDownloadTelemetrySink().Passed);
    }

    [Fact]
    public void TelemetrySink_PluginLocalImpl_Fails()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(RogueTelemetrySink) },
        };
        var r = h.RunDownloadTelemetrySink();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("RogueTelemetrySink"));
    }

    [Fact]
    public void TelemetrySink_CommonSink_Passes()
    {
        // common's own LoggingDownloadTelemetrySink lives under Lidarr.Plugin.Common.* so it is
        // accepted (this also covers the ILRepack-internalized case).
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(LoggingDownloadTelemetrySink) },
        };
        Assert.True(h.RunDownloadTelemetrySink().Passed);
    }

    // --- Check_DownloadClientUsesPathTraversalGuard ---

    [Fact]
    public void PathTraversalGuard_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunPathTraversalGuard().Passed);
    }

    [Fact]
    public void PathTraversalGuard_NoDownloadClient_NotApplicable_Passes()
    {
        // A plugin with no IDownloadClient (e.g. an import-list plugin) is not applicable.
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunPathTraversalGuard().Passed);
    }

    [Fact]
    public void PathTraversalGuard_HasDownloadClient_AndAssemblyReferencesGuard_Passes()
    {
        // The tests assembly references PathTraversalGuard (PathTraversalGuardTests), so a plugin
        // that ships a download client AND references the guard passes. (Mirrors the
        // Check_RegistersBridgeDefaults assembly-metadata-scan approach; a true-negative cannot be
        // synthesized within a single assembly that already contains the string.)
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
        };
        Assert.True(h.RunPathTraversalGuard().Passed);
    }

    // --- Check_DownloadClientStampsRegisteredClientId ---
    //
    // Contract (live qobuz regression, 2026-05-31): a download client's GetItems() must stamp every
    // reported DownloadClientItem with DownloadClientInfo.Id == this client's Definition.Id (never a
    // literal 0). Lidarr resolves the owning client by that id; a 0/wrong id makes CompletedDownloadService
    // throw "Sequence contains no matching element" and wedges every completed download before import.
    // Canonical shape (tidal/apple/amazon): DownloadClientItemClientInfo.FromDownloadClient(this, …);
    // qobuz derives the id explicitly from Definition?.Id. Both satisfy the guard.

    private const string DlClientHeader =
        "namespace P;\nusing System.Collections.Generic;\nusing System.Linq;\n";

    [Fact]
    public void DownloadClientIdStamp_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunDownloadClientIdStamp().Passed);
    }

    [Fact]
    public void DownloadClientIdStamp_NoDownloadClient_NotApplicable_Passes()
    {
        // Import-list plugins (e.g. brainarr) ship no download client → N/A.
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunDownloadClientIdStamp().Passed);
    }

    [Fact]
    public void DownloadClientIdStamp_HasClientButNoSourceFile_Passes()
    {
        // Ships a download client but no download-client source file is found under the source root
        // (relocated/examples-only source) → don't false-fail; the GetItems() body can't be located.
        var src = Path.Combine(_tempRepo, "empty-src");
        Directory.CreateDirectory(src);
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        Assert.True(h.RunDownloadClientIdStamp().Passed);
    }

    [Fact]
    public void DownloadClientIdStamp_CanonicalFromDownloadClient_Passes()
    {
        // tidal/apple/amazon shape — the host helper always reads this.Definition.Id/Name.
        // The interpolated Title exercises the brace-matcher against balanced interpolation braces.
        var src = Path.Combine(_tempRepo, "src-canonical");
        WriteSrcFile(src, "FooDownloadClient.cs",
            DlClientHeader +
            "public class FooDownloadClient : DownloadClientBase<FooSettings>\n{\n" +
            "    public override IEnumerable<DownloadClientItem> GetItems()\n    {\n" +
            "        var result = new List<DownloadClientItem>();\n" +
            "        foreach (var item in Tracker.GetSnapshot())\n        {\n" +
            "            result.Add(new DownloadClientItem\n            {\n" +
            "                DownloadId = item.Id,\n" +
            "                Title = $\"{item.Artist} - {item.Title}\",\n" +
            "                DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false)\n" +
            "            });\n        }\n        return result;\n    }\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunDownloadClientIdStamp();
        Assert.True(r.Passed, string.Join("; ", r.Errors));
    }

    [Fact]
    public void DownloadClientIdStamp_DerivesIdFromDefinition_Passes()
    {
        // qobuz shape — hand-rolled converter, but the client id is derived from Definition?.Id.
        var src = Path.Combine(_tempRepo, "src-definition");
        WriteSrcFile(src, "BarDownloadClient.cs",
            DlClientHeader +
            "public class BarDownloadClient : DownloadClientBase<BarSettings>\n{\n" +
            "    public override IEnumerable<DownloadClientItem> GetItems()\n    {\n" +
            "        var clientId = Definition?.Id ?? 0;\n" +
            "        var clientName = Definition?.Name ?? Name;\n" +
            "        return Tracker.GetSnapshot().Select(i => i.ToDownloadClientItem(clientId, clientName)).ToList();\n" +
            "    }\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunDownloadClientIdStamp();
        Assert.True(r.Passed, string.Join("; ", r.Errors));
    }

    [Fact]
    public void DownloadClientIdStamp_StampsLiteralZero_Fails()
    {
        // The exact historical regression: passing literal 0 as the client id, no Definition derivation.
        var src = Path.Combine(_tempRepo, "src-zero");
        WriteSrcFile(src, "BadDownloadClient.cs",
            DlClientHeader +
            "public class BadDownloadClient : DownloadClientBase<BadSettings>\n{\n" +
            "    public override IEnumerable<DownloadClientItem> GetItems()\n    {\n" +
            "        return Tracker.GetSnapshot().Select(i => i.ToDownloadClientItem(0, Name)).ToList();\n" +
            "    }\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunDownloadClientIdStamp();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("BadDownloadClient"));
    }

    [Fact]
    public void DownloadClientIdStamp_GetItemsIgnoresDefinition_Fails()
    {
        // GetItems builds client-info without deriving the id from Definition nor FromDownloadClient(this).
        var src = Path.Combine(_tempRepo, "src-ignore");
        WriteSrcFile(src, "IgnDownloadClient.cs",
            DlClientHeader +
            "public class IgnDownloadClient : DownloadClientBase<IgnSettings>\n{\n" +
            "    public override IEnumerable<DownloadClientItem> GetItems()\n    {\n" +
            "        var result = new List<DownloadClientItem>();\n" +
            "        result.Add(new DownloadClientItem { DownloadId = \"x\", DownloadClientInfo = new DownloadClientItemClientInfo { Name = \"Foo\" } });\n" +
            "        return result;\n" +
            "    }\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunDownloadClientIdStamp();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("IgnDownloadClient"));
    }

    // --- Check_DownloadClientUsesCommonPayloadValidator ---
    //
    // Audio-payload validation is consolidated on Common's DownloadPayloadValidator; a download-client
    // plugin must show positive Common validation evidence and must not use the legacy
    // AudioMagicBytesValidator nor declare its own *PayloadValidator fork.

    [Fact]
    public void PayloadValidator_NoAssembly_ReturnsSkipped()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        Assert.True(h.RunPayloadValidator().Passed);
    }

    [Fact]
    public void PayloadValidator_NoDownloadClient_NotApplicable_Passes()
    {
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FileTokenStore<string>) },
        };
        Assert.True(h.RunPayloadValidator().Passed);
    }

    [Fact]
    public void PayloadValidator_UsesCommonValidator_Passes()
    {
        var src = Path.Combine(_tempRepo, "pv-clean");
        WriteSrcFile(src, "FooDownloadClient.cs",
            "namespace P;\npublic class FooDownloadClient : DownloadClientBase<FooSettings>\n{\n" +
            "    void Validate(System.ReadOnlySpan<byte> b) => DownloadPayloadValidator.ValidateOrThrow(b, \".flac\", null);\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunPayloadValidator();
        Assert.True(r.Passed, string.Join("; ", r.Errors));
    }

    [Fact]
    public void PayloadValidator_UsesCommonDownloadService_Passes()
    {
        var src = Path.Combine(_tempRepo, "pv-common-service");
        WriteSrcFile(src, "FooDownloadClient.cs",
            "namespace P;\npublic class FooDownloadClient : DownloadClientBase<FooSettings>\n{\n" +
            "    object Build() => new SimpleDownloadOrchestrator();\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunPayloadValidator();
        Assert.True(r.Passed, string.Join("; ", r.Errors));
    }

    [Fact]
    public void PayloadValidator_DownloadClientWithoutCommonValidator_Fails()
    {
        var src = Path.Combine(_tempRepo, "pv-missing");
        WriteSrcFile(src, "FooDownloadClient.cs",
            "namespace P;\npublic class FooDownloadClient : DownloadClientBase<FooSettings>\n{\n" +
            "    void Save(System.ReadOnlySpan<byte> b) { }\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunPayloadValidator();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("DownloadPayloadValidator"));
    }

    [Fact]
    public void PayloadValidator_UsesLegacyAudioMagicBytesValidator_Fails()
    {
        var src = Path.Combine(_tempRepo, "pv-legacy");
        WriteSrcFile(src, "FooDownloadClient.cs",
            "namespace P;\npublic class FooDownloadClient : DownloadClientBase<FooSettings>\n{\n" +
            "    bool Validate(byte[] b) => AudioMagicBytesValidator.IsValidAudioMagicBytes(b.AsSpan());\n}\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunPayloadValidator();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("AudioMagicBytesValidator"));
    }

    [Fact]
    public void PayloadValidator_DeclaresLocalForkClass_Fails()
    {
        var src = Path.Combine(_tempRepo, "pv-fork");
        WriteSrcFile(src, "RogueDownloadPayloadValidator.cs",
            "namespace P;\ninternal static class RogueDownloadPayloadValidator\n{\n" +
            "    public static void ValidateOrThrow(System.ReadOnlySpan<byte> s, string? e, string? m) { }\n}\n");
        // The download client itself lives in another file so the gate is satisfied.
        WriteSrcFile(src, "FooDownloadClient.cs",
            "namespace P;\npublic class FooDownloadClient : DownloadClientBase<FooSettings> { }\n");
        var h = new Harness(_tempRepo)
        {
            AssemblyValue = typeof(EcosystemParityTestBaseExtensionTests).Assembly,
            TypesValue = new[] { typeof(FakeDownloadClient) },
            SourceRootValue = src,
        };
        var r = h.RunPayloadValidator();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("PayloadValidator fork"));
    }

    // --- Check_SimpleDownloadOrchestratorCoverArtComplianceAdopted ---

    [Fact]
    public void CoverArtCompliance_NoSimpleDownloadOrchestratorUsage_Passes()
    {
        var src = Path.Combine(_tempRepo, "cover-no-orch-src");
        WriteSrcFile(src, "DownloadClient.cs",
            "namespace P;\npublic sealed class DownloadClient { void Start() { } }\n");

        var h = new Harness(_tempRepo)
        {
            SourceRootValue = src,
        };

        Assert.True(h.RunCoverArtEmbeddingComplianceAdoption().Passed);
    }

    [Fact]
    public void CoverArtCompliance_UsesSimpleDownloadOrchestratorWithoutComplianceTest_Fails()
    {
        var src = Path.Combine(_tempRepo, "cover-orch-no-adoption-src");
        Directory.CreateDirectory(Path.Combine(_tempRepo, "tests"));
        WriteSrcFile(src, "DownloadClient.cs",
            "namespace P;\npublic sealed class DownloadClient\n{\n" +
            "    object Build() => new SimpleDownloadOrchestrator();\n}\n");

        var h = new Harness(_tempRepo)
        {
            SourceRootValue = src,
        };

        var r = h.RunCoverArtEmbeddingComplianceAdoption();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("CoverArtEmbeddingComplianceTestBase"));
    }

    [Fact]
    public void CoverArtCompliance_UsesSimpleDownloadOrchestratorWithComplianceTest_Passes()
    {
        var src = Path.Combine(_tempRepo, "cover-orch-adopted-src");
        WriteSrcFile(src, "QobuzDownloadOrchestrator.cs",
            "namespace P;\npublic sealed class QobuzDownloadOrchestrator : SimpleDownloadOrchestrator { }\n");
        WriteSrcFile(Path.Combine(_tempRepo, "tests"), "CoverArtTests.cs",
            "namespace P.Tests;\npublic sealed class CoverArtTests : CoverArtEmbeddingComplianceTestBase { }\n");

        var h = new Harness(_tempRepo)
        {
            SourceRootValue = src,
        };

        var r = h.RunCoverArtEmbeddingComplianceAdoption();
        Assert.True(r.Passed, string.Join("; ", r.Errors));
    }

    // --- Check_FileClassNameParity ---

    private string WriteSrcFile(string srcDir, string fileName, string content)
    {
        Directory.CreateDirectory(srcDir);
        var path = Path.Combine(srcDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void FileClassParity_NoSourceRoot_Passes()
    {
        var h = new Harness(_tempRepo) { SourceRootValue = Path.Combine(_tempRepo, "missing-src") };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_MatchingName_Passes()
    {
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Foo.cs", "namespace N;\npublic sealed class Foo { }");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_SingleTypeMismatch_Fails()
    {
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Bar.cs", "namespace N;\npublic class Different { }");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        var r = h.RunFileClassNameParity();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("Bar") && e.Contains("Different"));
    }

    [Fact]
    public void FileClassParity_RepoRootFallbackExcludesTestProjects()
    {
        WriteSrcFile(Path.Combine(_tempRepo, "Plugin"), "PluginEntry.cs", "namespace N;\npublic sealed class PluginEntry { }");
        WriteSrcFile(Path.Combine(_tempRepo, "tests", "Plugin.Tests"), "Helpers.cs", "namespace N;\npublic sealed class VoidResult { }");
        WriteSrcFile(Path.Combine(_tempRepo, "Plugin.Tests"), "RegistryTestCollection.cs", "namespace N;\npublic sealed class RegistryModelTestsCollection { }");

        var h = new Harness(_tempRepo) { SourceRootValue = _tempRepo };

        Assert.True(h.RunFileClassNameParity().Passed);
    }
    [Fact]
    public void FileClassParity_InternalPrimaryWithPublicNestedHelper_Passes()
    {
        // A file whose TOP-LEVEL type matches the file name (here `internal`) is correctly named even
        // if its only PUBLIC type is a nested helper — the *HealthDiagnostics pattern (internal
        // HealthDiagnostics + public nested Capabilities). Must NOT be flagged on the nested type.
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "QobuzHealthDiagnostics.cs",
            "namespace N;\ninternal static class QobuzHealthDiagnostics\n{\n    public static class Capabilities { }\n}");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_BlockNamespaceInternalPrimaryWithPublicNestedHelper_Passes()
    {
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "TokenBudgetResolver.cs",
            "namespace N\n{\n    internal sealed class TokenBudgetResolver\n    {\n        public sealed record PromptBudget;\n    }\n}");
        var h = new Harness(_tempRepo) { SourceRootValue = src };

        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_MultiTypeGroupingFile_Passes()
    {
        // DTO/exception-family grouping files declare >1 public type — allowed, not flagged.
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Dtos.cs", "namespace N;\npublic record A();\npublic record B();");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_PartialType_Passes()
    {
        // Partial types legitimately span multiple files with different names.
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Split.cs", "namespace N;\npublic partial class Engine { }");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_DottedAndGeneratedFileNames_Skipped()
    {
        // Dotted file names (X.Designer.cs / X.g.cs / partial splits) are skipped.
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Widget.Designer.cs", "namespace N;\npublic class Generated { }");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_GenericType_MatchesOnBareName_Passes()
    {
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Cache.cs", "namespace N;\npublic class Cache<TKey, TValue> { }");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    [Fact]
    public void FileClassParity_NoPublicTopLevelType_Passes()
    {
        var src = Path.Combine(_tempRepo, "src");
        WriteSrcFile(src, "Internals.cs", "namespace N;\ninternal class Helper { }");
        var h = new Harness(_tempRepo) { SourceRootValue = src };
        Assert.True(h.RunFileClassNameParity().Passed);
    }

    // --- Check_ClaudeMdDocumentsCommonHelpers ---

    [Fact]
    public void ClaudeMd_MissingFile_Skipped()
    {
        var h = new Harness(_tempRepo); // _tempRepo has no CLAUDE.md
        Assert.True(h.RunClaudeMdHelpers().Passed);
    }

    [Fact]
    public void ClaudeMd_WithHelpersSection_Passes()
    {
        File.WriteAllText(Path.Combine(_tempRepo, "CLAUDE.md"),
            "# CLAUDE.md\n\n## Common helpers in use\n- PluginConfigRoots\n");
        var h = new Harness(_tempRepo);
        Assert.True(h.RunClaudeMdHelpers().Passed);
    }

    [Fact]
    public void ClaudeMd_WithoutHelpersSection_Fails()
    {
        File.WriteAllText(Path.Combine(_tempRepo, "CLAUDE.md"),
            "# CLAUDE.md\n\n## Build Commands\ndotnet build\n");
        var h = new Harness(_tempRepo);
        var r = h.RunClaudeMdHelpers();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("Common helpers in use"));
    }

    // --- DirectoryPackagesProps_HostVersionsMatchCanonical ---

    [Fact]
    public void HostVersions_MatchingCanonical_Passes()
    {
        File.WriteAllText(Path.Combine(_tempRepo, "Directory.Packages.props"),
            "<Project><ItemGroup>"
            + "<PackageVersion Include=\"NLog\" Version=\"5.4.0\" />"
            + "<PackageVersion Include=\"FluentValidation\" Version=\"9.5.4\" />"
            + "<PackageVersion Include=\"Microsoft.Extensions.Http\" Version=\"8.0.1\" />"
            + "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />" // not host-coupled — ignored
            + "</ItemGroup></Project>");
        var h = new Harness(_tempRepo);
        Assert.True(h.DirectoryPackagesProps_HostVersionsMatchCanonical().Passed);
    }

    [Fact]
    public void HostVersions_MismatchedHostCoupled_Fails()
    {
        File.WriteAllText(Path.Combine(_tempRepo, "Directory.Packages.props"),
            "<Project><ItemGroup>"
            + "<PackageVersion Include=\"NLog\" Version=\"6.0.3\" />" // host-coupled drift (host ships 5.x)
            + "</ItemGroup></Project>");
        var h = new Harness(_tempRepo);
        var r = h.DirectoryPackagesProps_HostVersionsMatchCanonical();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("NLog") && e.Contains("5.4.0"));
    }

    [Fact]
    public void HostVersions_AllSixCanonicalPackagesPresent_Passes()
    {
        // Characterization: proves all six canonical keys are wired correctly (a typo in any
        // dictionary key would let a wrong version slip through for that package).
        File.WriteAllText(Path.Combine(_tempRepo, "Directory.Packages.props"),
            "<Project><ItemGroup>"
            + "<PackageVersion Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"8.0.1\" />"
            + "<PackageVersion Include=\"Microsoft.Extensions.Logging\" Version=\"8.0.1\" />"
            + "<PackageVersion Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"8.0.3\" />"
            + "<PackageVersion Include=\"Microsoft.Extensions.Http\" Version=\"8.0.1\" />"
            + "<PackageVersion Include=\"FluentValidation\" Version=\"9.5.4\" />"
            + "<PackageVersion Include=\"NLog\" Version=\"5.4.0\" />"
            + "</ItemGroup></Project>");
        var h = new Harness(_tempRepo);
        var r = h.DirectoryPackagesProps_HostVersionsMatchCanonical();
        Assert.True(r.Passed, string.Join("; ", r.Errors));
    }

    [Fact]
    public void HostVersions_NoHostCoupledPackagesDeclared_Passes()
    {
        // "Absence is OK" is intentional: this guard is the DECLARED-PINS layer. A plugin that
        // doesn't declare a host-coupled package (e.g. applemusicarr omits
        // Microsoft.Extensions.DependencyInjection, which resolves transitively) is validated at
        // the RESOLVED-VERSION layer by the per-plugin host-DLL-grounded HostVersionCouplingTests.
        File.WriteAllText(Path.Combine(_tempRepo, "Directory.Packages.props"),
            "<Project><ItemGroup>"
            + "<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
            + "</ItemGroup></Project>");
        var h = new Harness(_tempRepo);
        Assert.True(h.DirectoryPackagesProps_HostVersionsMatchCanonical().Passed);
    }

    [Fact]
    public void HostVersions_NonLiteralVersionExpression_FailsLoud()
    {
        // A host-coupled package pinned to an MSBuild expression can't be statically verified for
        // parity (and defeats the greppability the literal-pin philosophy depends on). The guard
        // must fail with an actionable message, not silently miscompare the raw "$(...)" string.
        File.WriteAllText(Path.Combine(_tempRepo, "Directory.Packages.props"),
            "<Project><ItemGroup>"
            + "<PackageVersion Include=\"NLog\" Version=\"$(NLogVersion)\" />"
            + "</ItemGroup></Project>");
        var h = new Harness(_tempRepo);
        var r = h.DirectoryPackagesProps_HostVersionsMatchCanonical();
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("NLog") && e.Contains("literal"));
    }

    // --- Aggregator ---

    [Fact]
    public void RunBehaviorContractChecks_AllCheckResultsPresent()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        var report = h.RunBehaviorContractChecks();
        Assert.Equal(16, report.TotalCount);
        // No assembly + no CLAUDE.md => the other 15 skip (Pass); Check_EnforcesAlbumCompletionPolicy
        // runs unconditionally (it asserts the shared rule directly, no assembly needed) and passes.
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void RunAllParityChecks_IncludesEveryBehaviorContractCheck()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };

        var all = h.RunAllParityChecks();
        var behavior = h.RunBehaviorContractChecks();

        var missing = behavior.Results.Keys
            .Where(key => !all.Results.ContainsKey(key))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EcosystemParityBase_ExposesInheritedAggregateFact()
    {
        var method = typeof(EcosystemParityTestBase).GetMethod("AllParityChecksPass");

        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<FactAttribute>());
    }
}
