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

    // --- Aggregator ---

    [Fact]
    public void RunBehaviorContractChecks_AllCheckResultsPresent()
    {
        var h = new Harness(_tempRepo) { AssemblyValue = null };
        var report = h.RunBehaviorContractChecks();
        Assert.Equal(6, report.TotalCount);
        // No assembly => all 6 skipped (Pass).
        Assert.True(report.AllPassed);
    }
}
