using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Behavior-contract checks for <see cref="EcosystemParityTestBase"/>. These extend the
/// structural parity contract with cross-plugin invariants established by the unification
/// work (Phases 1-4): token storage, response cache, bridge defaults, capability backing,
/// FluentValidation API stability, and config-root usage.
/// </summary>
/// <remarks>
/// <para>
/// Each behavior check is a <c>protected virtual</c> method returning
/// <see cref="ComplianceResult"/>. Plugins may override an individual check to document a
/// known divergence with an explicit rationale (a passing override that returns
/// <see cref="ComplianceResult.Success"/> with a comment, or an in-test attribute).
/// </para>
/// <para>
/// All checks rely on an opt-in <see cref="PluginAssembly"/> hook. When the plugin test
/// class does not supply an assembly, behavior checks return <see cref="ComplianceResult.Success"/>
/// (skipped) so that legacy plugins are not broken on a submodule pin bump. Plugins opt in
/// by overriding <see cref="PluginAssembly"/> to return their compiled assembly.
/// </para>
/// </remarks>
public abstract partial class EcosystemParityTestBase
{
    #region Opt-in Hooks

    /// <summary>
    /// Optional plugin assembly used by behavior checks. Override in the per-plugin
    /// subclass (e.g. <c>typeof(BrainarrPluginModule).Assembly</c>) to opt in. When null,
    /// behavior checks return Success (skipped). This keeps legacy plugins green on
    /// submodule bumps and lets each plugin opt in deliberately.
    /// </summary>
    protected virtual Assembly? PluginAssembly => null;

    /// <summary>
    /// Source root for text-scanning checks. Defaults to <see cref="RepoRootPath"/>'s
    /// <c>src/</c> folder if present, else the repo root.
    /// </summary>
    protected virtual string PluginSourceRoot
    {
        get
        {
            var candidate = Path.Combine(RepoRootPath, "src");
            return Directory.Exists(candidate) ? candidate : RepoRootPath;
        }
    }

    private static ComplianceResult Skipped() => ComplianceResult.Success;

    /// <summary>
    /// Enumerates the types in the plugin assembly. Overridable for tests that wish to
    /// inject a curated type set without fabricating an assembly.
    /// </summary>
    protected virtual IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    #endregion

    #region Check_UsesCommonFileTokenStore

    /// <summary>
    /// Plugins must use common's <c>FileTokenStore&lt;&gt;</c> rather than rolling their
    /// own <c>ITokenStore&lt;&gt;</c> implementation. This closes the plaintext-refresh-token
    /// security gap that motivated the Phase 2 unification. Plugin-local types implementing
    /// <c>ITokenStore&lt;TSession&gt;</c> fail this check.
    /// </summary>
    public virtual ComplianceResult Check_UsesCommonFileTokenStore()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        const string commonFullName = "Lidarr.Plugin.Common.Services.Authentication.FileTokenStore`1";
        var offenders = new List<string>();
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type == null) continue;
            bool isInterfaceOrAbstract;
            try { isInterfaceOrAbstract = type.IsInterface || type.IsAbstract; }
            catch { continue; }
            if (isInterfaceOrAbstract) continue;

            // Refinement: any type whose namespace lives under common's namespace tree is
            // acceptable, regardless of host assembly. After ILRepack internalizes common's
            // types into the merged plugin DLL, their FullName/Namespace still starts with
            // "Lidarr.Plugin.Common." even though the assembly is the plugin's.
            string ns;
            try { ns = type.Namespace ?? string.Empty; }
            catch { continue; } // type's enclosing/declaring type can't load — skip safely
            if (IsCommonProductionNamespace(ns)) continue;

            // Refinement: explicit opt-in attribute for legitimate fallback / fail-fast
            // patterns (e.g. tidalarr's FailOnIOTokenStore).
            if (HasParityAllowedTokenStoreAttribute(type)) continue;

            Type[] ifaces;
            try { ifaces = type.GetInterfaces(); }
            catch { continue; }
            foreach (var iface in ifaces)
            {
                if (!iface.IsGenericType) continue;
                var def = iface.GetGenericTypeDefinition();
                if (def.FullName == "Lidarr.Plugin.Common.Interfaces.ITokenStore`1")
                {
                    // Walk up base chain — common's FileTokenStore<T> is acceptable.
                    var ok = false;
                    try
                    {
                        var t = type;
                        while (t != null)
                        {
                            var fn = t.IsGenericType ? t.GetGenericTypeDefinition().FullName : t.FullName;
                            if (fn == commonFullName) { ok = true; break; }
                            t = t.BaseType;
                        }
                    }
                    catch { ok = false; }
                    if (!ok) offenders.Add(type.FullName ?? type.Name);
                }
            }
        }
        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Plugin-local ITokenStore<> implementations are forbidden — use Lidarr.Plugin.Common.Services.Authentication.FileTokenStore<> (or mark with [ParityAllowedTokenStore(\"rationale\")] for fallback patterns): {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Returns true if the namespace belongs to common's production code tree (i.e. types
    /// that ILRepack would internalize into a plugin's merged DLL). Excludes test/testkit
    /// sub-trees so test fixtures with namespaces under <c>Lidarr.Plugin.Common.Tests.*</c>
    /// are still subject to the check.
    /// </summary>
    private static bool IsCommonProductionNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return false;
        if (!ns.StartsWith("Lidarr.Plugin.Common.", StringComparison.Ordinal) &&
            !ns.Equals("Lidarr.Plugin.Common", StringComparison.Ordinal)) return false;
        // Exclude test-side namespaces.
        if (ns.StartsWith("Lidarr.Plugin.Common.Tests", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("Lidarr.Plugin.Common.TestKit", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool HasParityAllowedTokenStoreAttribute(Type type)
    {
        // Match by full name so the check works whether or not the attribute type itself
        // has been internalized into the plugin's assembly via ILRepack.
        const string attrFullName = "Lidarr.Plugin.Common.TestKit.Compliance.ParityAllowedTokenStoreAttribute";
        try
        {
            foreach (var attr in type.GetCustomAttributesData())
            {
                if (attr.AttributeType.FullName == attrFullName) return true;
            }
        }
        catch
        {
            // Some reflection-only contexts can throw; fall back to runtime attribute query.
            try
            {
                return type.GetCustomAttributes(inherit: false)
                    .Any(a => a.GetType().FullName == attrFullName);
            }
            catch { return false; }
        }
        return false;
    }

    #endregion

    #region Check_UsesCommonHttpResponseCache

    /// <summary>
    /// Plugins must use common's <c>IStreamingResponseCache</c> implementation
    /// (post-Phase 3 unification). A plugin-local class directly implementing
    /// <c>IStreamingResponseCache</c> indicates a fork.
    /// </summary>
    public virtual ComplianceResult Check_UsesCommonHttpResponseCache()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        const string ifaceFullName = "Lidarr.Plugin.Common.Interfaces.IStreamingResponseCache";

        // Acceptable bases: subclassing one of these is the supported extension pattern
        // (endpoint-specific cache keys/durations). Direct IStreamingResponseCache impls
        // without inheriting from a common base are still flagged as forks.
        var acceptableBaseFullNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Lidarr.Plugin.Common.Services.Caching.StreamingResponseCache",
            "Lidarr.Plugin.Common.Services.Caching.FileStreamingResponseCache",
        };

        var offenders = new List<string>();
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type == null) continue;
            try { if (type.IsInterface || type.IsAbstract) continue; }
            catch { continue; }
            Type[] ifaces;
            try { ifaces = type.GetInterfaces(); }
            catch { continue; }
            if (!ifaces.Any(i => i.FullName == ifaceFullName)) continue;

            // Refinement: types living under common's namespace are acceptable (handles
            // ILRepack-internalized common types).
            string ns;
            try { ns = type.Namespace ?? string.Empty; }
            catch { continue; }
            if (IsCommonProductionNamespace(ns)) continue;

            // Refinement: walk base chain. If we hit a common base class, the type is a
            // legitimate subclass extension, not a fork.
            var inheritsFromCommonBase = false;
            try
            {
                var b = type.BaseType;
                while (b != null)
                {
                    var bfn = b.IsGenericType ? b.GetGenericTypeDefinition().FullName : b.FullName;
                    if (bfn != null && acceptableBaseFullNames.Contains(bfn))
                    {
                        inheritsFromCommonBase = true;
                        break;
                    }
                    b = b.BaseType;
                }
            }
            catch { /* unresolved bases — treat as not-inheriting */ }
            if (inheritsFromCommonBase) continue;

            offenders.Add(type.FullName ?? type.Name);
        }
        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Plugin-local IStreamingResponseCache implementations are forbidden — subclass StreamingResponseCache/FileStreamingResponseCache or register common's via DI: {string.Join(", ", offenders)}");
    }

    #endregion

    #region Check_RegistersBridgeDefaults

    /// <summary>
    /// Plugins that subclass <c>StreamingPluginModule</c> should call
    /// <c>AddBridgeDefaults()</c> in their <c>ConfigureServices</c> override. We detect the
    /// reference at the assembly level (an <c>AssemblyRef</c> or member-reference to the
    /// extension method); plugins that don't subclass <c>StreamingPluginModule</c> are not
    /// applicable and pass.
    /// </summary>
    public virtual ComplianceResult Check_RegistersBridgeDefaults()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        const string moduleBaseFullName = "Lidarr.Plugin.Common.Services.Registration.StreamingPluginModule";
        var hasModule = SafeGetTypes(assembly).Any(t =>
        {
            if (t == null) return false;
            var b = t.BaseType;
            while (b != null)
            {
                if (b.FullName == moduleBaseFullName) return true;
                b = b.BaseType;
            }
            return false;
        });
        if (!hasModule) return ComplianceResult.Success; // not applicable

        // Heuristic: scan the assembly's referenced module/extension by reading the manifest.
        // Reflection alone cannot see method body call instructions without IL inspection;
        // we settle for a string-presence check on the assembly's metadata names. The
        // assembly will reference Lidarr.Plugin.Common.Extensions.BridgeServiceCollectionExtensions
        // if AddBridgeDefaults is invoked from any method body.
        var bytes = File.ReadAllBytes(assembly.Location);
        // Convert bytes to string with a permissive encoding so we can find UTF-8 method
        // name strings in the metadata heap.
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (text.Contains("AddBridgeDefaults", StringComparison.Ordinal))
            return ComplianceResult.Success;

        return ComplianceResult.Failure(
            "Plugin subclasses StreamingPluginModule but does not appear to call AddBridgeDefaults() — bridge default services (auth-failure, status, rate-limit) will be missing.");
    }

    #endregion

    #region Check_PluginManifest_Capabilities_HaveBackingTypes

    /// <summary>
    /// Capability flags declared in <c>plugin.json</c> must have backing implementations.
    /// Currently checked: <c>ProvidesIndexer</c> ⇒ at least one type implementing
    /// <c>IIndexer</c>; <c>ProvidesDownloadClient</c> ⇒ at least one type implementing
    /// <c>IDownloadClient</c>. Other flags (e.g. <c>SupportsOAuth</c>) are skipped because
    /// they lack a single canonical type.
    /// </summary>
    public virtual ComplianceResult Check_PluginManifest_Capabilities_HaveBackingTypes()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();
        if (!FileExists(PluginJsonRelativePath)) return Skipped();

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array)
            return ComplianceResult.Success; // optional

        var declared = caps.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

        var checks = new (string Flag, string IfaceFullName)[]
        {
            ("ProvidesIndexer", "Lidarr.Plugin.Abstractions.Contracts.IIndexer"),
            ("ProvidesDownloadClient", "Lidarr.Plugin.Abstractions.Contracts.IDownloadClient"),
        };

        var errors = new List<string>();
        foreach (var (flag, ifaceFullName) in checks)
        {
            if (!declared.Contains(flag)) continue;
            var found = SafeGetTypes(assembly).Any(t =>
                t != null && !t.IsInterface && !t.IsAbstract &&
                t.GetInterfaces().Any(i => i.FullName == ifaceFullName));
            if (!found)
                errors.Add($"plugin.json declares capability '{flag}' but no concrete type implements {ifaceFullName}");
        }
        return errors.Count == 0 ? ComplianceResult.Success : new ComplianceResult(false, errors);
    }

    #endregion

    #region Check_NoFluentValidation_ErrorsApi_Drift

    /// <summary>
    /// FluentValidation's <c>ValidationResult.Errors</c> getter signature drifted between
    /// 9.x and 11.x and caused a host crash. Plugins should rely on the stable
    /// <c>ToString()</c> / <c>IEnumerable&lt;ValidationFailure&gt;</c> ctor pattern. This
    /// check scans plugin source files for direct <c>.Errors</c> getter usage on a
    /// <c>ValidationResult</c> as a soft warning.
    /// </summary>
    public virtual ComplianceResult Check_NoFluentValidation_ErrorsApi_Drift()
    {
        if (!Directory.Exists(PluginSourceRoot)) return Skipped();

        var offenders = new List<string>();
        var excluded = new[]
        {
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}ext{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            // Test projects use FluentValidation legitimately to exercise host behavior.
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            ".Tests" + Path.DirectorySeparatorChar,
        };
        foreach (var file in Directory.EnumerateFiles(PluginSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (excluded.Any(x => file.Contains(x, StringComparison.Ordinal))) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (!text.Contains("FluentValidation", StringComparison.Ordinal)) continue;
            if (!text.Contains("ValidationResult", StringComparison.Ordinal)) continue;
            // Refinement: flag only the unstable getter pattern — LINQ chaining off `.Errors`
            // depends on the getter return type that drifted between FV 9.x and 11.x.
            // Allow `.Errors.Add(...)` and similar list-mutation calls because those work on
            // either IList<T> or List<T>. Allow `.Errors.Count` (property) but flag
            // `.Errors.Count(...)` (LINQ extension) by requiring a parenthesis.
            //
            // Pattern: <ident>.Errors. followed by Select|Where|Any|Count|ToList|ToArray|
            // FirstOrDefault|First|SingleOrDefault|Single (LINQ-ish), with an opening paren
            // for the call form.
            var unstableGetterPattern = new System.Text.RegularExpressions.Regex(
                @"\b\w+\.Errors\.(Select|Where|Any|Count|ToList|ToArray|FirstOrDefault|First|SingleOrDefault|Single|Aggregate|GroupBy|OrderBy|OrderByDescending)\s*\(",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            if (unstableGetterPattern.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRootPath, file));
            }
        }
        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Direct ValidationResult.Errors getter usage detected — prefer the stable IEnumerable<ValidationFailure> ctor / ToString() pattern (FV 9.x↔11.x drift): {string.Join(", ", offenders)}");
    }

    #endregion

    #region Check_UsesCommonPluginConfigRoots

    /// <summary>
    /// Plugins must use <c>Lidarr.Plugin.Common.Hosting.PluginConfigRoots.Resolve(...)</c>
    /// rather than a plugin-local config-paths helper. Detected by scanning for any
    /// plugin-local class named like <c>*ConfigPathDefaults</c> / <c>*ConfigPaths</c>.
    /// </summary>
    public virtual ComplianceResult Check_UsesCommonPluginConfigRoots()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        var offenders = SafeGetTypes(assembly)
            .Where(t => t != null)
            .Where(t =>
            {
                var n = t!.Name ?? "";
                return n.EndsWith("ConfigPathDefaults", StringComparison.Ordinal)
                    || n.EndsWith("ConfigPathResolver", StringComparison.Ordinal);
            })
            .Select(t => t!.FullName ?? t!.Name)
            .ToList();

        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Plugin-local config-path helpers detected — use Lidarr.Plugin.Common.Hosting.PluginConfigRoots.Resolve: {string.Join(", ", offenders)}");
    }

    #endregion

    #region Check_UsesCommonLyricsEnricher

    /// <summary>
    /// Synced-lyrics enrichment is consolidated in common
    /// (<c>Lidarr.Plugin.Common.Services.Lyrics.ILyricsEnricher</c> / <c>LyricsEnricher</c>).
    /// A plugin must not define its own <c>ILyricsEnricher</c>/<c>LyricsEnricher</c> (the
    /// historical qobuz/tidal duplication) nor fork the orchestration by implementing common's
    /// interface in plugin-local code — it consumes common's type directly. Service-specific
    /// fetches belong behind common's <c>INativeLyricsSource</c>, not a re-declared enricher.
    /// </summary>
    public virtual ComplianceResult Check_UsesCommonLyricsEnricher()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        const string commonInterfaceFullName = "Lidarr.Plugin.Common.Services.Lyrics.ILyricsEnricher";
        var offenders = new List<string>();
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type == null) continue;
            string ns, name;
            try { ns = type.Namespace ?? string.Empty; name = type.Name ?? string.Empty; }
            catch { continue; }

            // Common's own types (incl. ILRepack-internalized) are the canonical implementation.
            if (IsCommonProductionNamespace(ns)) continue;

            // (a) A plugin-local re-declaration of the lyrics-enricher shape (the historical fork).
            if (name == "ILyricsEnricher" || name == "LyricsEnricher")
            {
                offenders.Add(type.FullName ?? name);
                continue;
            }

            // (b) A plugin-local fork that implements common's shared interface directly.
            try
            {
                if (type.IsInterface || type.IsAbstract) continue;
                if (type.GetInterfaces().Any(i => i.FullName == commonInterfaceFullName))
                {
                    offenders.Add(type.FullName ?? name);
                }
            }
            catch { /* unresolved interfaces — skip safely */ }
        }

        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Plugin defines its own lyrics enricher — consolidate on Lidarr.Plugin.Common.Services.Lyrics.ILyricsEnricher/LyricsEnricher (put service-specific fetches behind INativeLyricsSource): {string.Join(", ", offenders)}");
    }

    #endregion

    #region Aggregator

    /// <summary>
    /// Runs the behavior-contract checks. Returns a separate report so callers can decide
    /// whether to combine with structural results.
    /// </summary>
    public virtual ComplianceReport RunBehaviorContractChecks()
    {
        var results = new Dictionary<string, ComplianceResult>
        {
            [nameof(Check_UsesCommonFileTokenStore)] = Check_UsesCommonFileTokenStore(),
            [nameof(Check_UsesCommonHttpResponseCache)] = Check_UsesCommonHttpResponseCache(),
            [nameof(Check_RegistersBridgeDefaults)] = Check_RegistersBridgeDefaults(),
            [nameof(Check_PluginManifest_Capabilities_HaveBackingTypes)] = Check_PluginManifest_Capabilities_HaveBackingTypes(),
            [nameof(Check_NoFluentValidation_ErrorsApi_Drift)] = Check_NoFluentValidation_ErrorsApi_Drift(),
            [nameof(Check_UsesCommonPluginConfigRoots)] = Check_UsesCommonPluginConfigRoots(),
            [nameof(Check_UsesCommonLyricsEnricher)] = Check_UsesCommonLyricsEnricher(),
        };

        var passed = results.Values.Count(r => r.Passed);
        return new ComplianceReport(results, passed, results.Count);
    }

    #endregion
}
