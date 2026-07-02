using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Lidarr.Plugin.Common.Services.Download;

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

    #region Check_UsesCommonDiagnosticTypes

    /// <summary>
    /// Plugins must reference common's canonical
    /// <c>Lidarr.Plugin.Common.Abstractions.Diagnostics.DiagnosticTypes</c> +
    /// <c>DiagnosticErrorCodes</c> rather than re-declaring identical <c>DiagnosticTypes</c> /
    /// <c>ErrorCodes</c> nested classes inside their <c>*HealthDiagnostics</c> type. Those per-plugin
    /// copies (qobuz/tidal/apple all had them) drift independently — collapsing them to Common makes
    /// a rename land in one place. This flags a class named <c>DiagnosticTypes</c> or <c>ErrorCodes</c>
    /// nested in a <c>*HealthDiagnostics</c> type, outside common's own namespace.
    /// </summary>
    public virtual ComplianceResult Check_UsesCommonDiagnosticTypes()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        var offenders = new List<string>();
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type == null) continue;
            string name;
            try { name = type.Name ?? string.Empty; }
            catch { continue; }
            if (name != "DiagnosticTypes" && name != "ErrorCodes") continue;

            // Only the *HealthDiagnostics-nested copies are the consolidation target; an unrelated
            // top-level type that happens to share the name is out of scope.
            Type? declaring;
            try { declaring = type.DeclaringType; }
            catch { continue; }
            if (declaring == null || !(declaring.Name ?? string.Empty).EndsWith("HealthDiagnostics", StringComparison.Ordinal)) continue;

            // common's own types (incl. ILRepack-internalized) are fine.
            string ns;
            try { ns = type.Namespace ?? string.Empty; }
            catch { continue; }
            if (IsCommonProductionNamespace(ns)) continue;

            offenders.Add(type.FullName ?? name);
        }
        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Plugin-local DiagnosticTypes/ErrorCodes nested in *HealthDiagnostics are forbidden — reference Lidarr.Plugin.Common.Abstractions.Diagnostics.DiagnosticTypes + DiagnosticErrorCodes: {string.Join(", ", offenders)}");
    }

    #endregion

    #region Check_UsesCommonDownloadTelemetrySink

    /// <summary>
    /// Plugins must consume common's canonical download-telemetry sink
    /// (<c>LoggingDownloadTelemetrySink</c>, registered via <c>AddDownloadTelemetry()</c>) instead
    /// of hand-rolling an <c>IDownloadTelemetrySink</c>. A plugin-local implementation re-creates
    /// the per-track log format that lives in <c>DownloadTelemetryService</c>, re-introducing the
    /// cross-plugin logging divergence this contract exists to prevent. A genuinely custom sink
    /// (e.g. one that forwards telemetry to an external metrics backend instead of logging) is a
    /// legitimate divergence — document it by overriding this check to return
    /// <see cref="ComplianceResult.Success"/> with a rationale.
    /// </summary>
    public virtual ComplianceResult Check_UsesCommonDownloadTelemetrySink()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();

        const string ifaceFullName = "Lidarr.Plugin.Common.Services.Download.IDownloadTelemetrySink";
        const string commonSinkFullName = "Lidarr.Plugin.Common.Services.Download.LoggingDownloadTelemetrySink";

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

            // common's own types (incl. ILRepack-internalized LoggingDownloadTelemetrySink) are fine.
            string ns;
            try { ns = type.Namespace ?? string.Empty; }
            catch { continue; }
            if (IsCommonProductionNamespace(ns)) continue;

            // Belt-and-suspenders: allow the canonical sink by full name even if its namespace
            // classification ever changes.
            var fullName = type.FullName ?? type.Name;
            if (fullName == commonSinkFullName) continue;

            offenders.Add(fullName);
        }
        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Plugin-local IDownloadTelemetrySink implementations are forbidden — register common's via AddDownloadTelemetry() / LoggingDownloadTelemetrySink (or override this check to document a custom telemetry backend): {string.Join(", ", offenders)}");
    }

    #endregion

    #region Check_DownloadClientUsesPathTraversalGuard

    /// <summary>
    /// Plugins that ship a download client must sanitize user-supplied path segments through common's
    /// <c>PathTraversalGuard</c> (<c>SanitizeSegment</c> / <c>IsPathWithinRoot</c>) before writing to
    /// disk — otherwise a crafted artist/album/track name can escape the download root. This guard is
    /// applicable only when the plugin declares a download client (import-list plugins such as brainarr
    /// have no download path → N/A). Detection mirrors <see cref="Check_RegistersBridgeDefaults"/>: a
    /// reference to <c>PathTraversalGuard</c> in the (un-merged, in test context) plugin assembly.
    /// </summary>
    public virtual ComplianceResult Check_DownloadClientUsesPathTraversalGuard()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();
        if (!PluginDeclaresHostDownloadClient(assembly)) return ComplianceResult.Success; // not applicable

        string text;
        try { text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(assembly.Location)); }
        catch { return ComplianceResult.Success; } // unreadable — don't false-fail
        if (text.Contains("PathTraversalGuard", StringComparison.Ordinal))
            return ComplianceResult.Success;

        return ComplianceResult.Failure(
            "Plugin ships a download client but its assembly does not reference Lidarr.Plugin.Common.HostBridge.PathTraversalGuard — user-supplied path segments must be sanitized via PathTraversalGuard (SanitizeSegment / IsPathWithinRoot) to prevent path-traversal writes outside the download root.");
    }

    #endregion

    #region Check_DownloadClientUsesCommonPayloadValidator

    /// <summary>
    /// Audio-payload validation — is a downloaded blob real audio vs an HTML/JSON error page or the
    /// wrong container? — is consolidated in Common's canonical <c>DownloadPayloadValidator</c>, a strict
    /// superset of the legacy <c>AudioMagicBytesValidator</c> (it adds MP4/M4A ftyp recognition,
    /// text/HTML/JSON/XML rejection, and file + span overloads). A plugin that ships a download client
    /// must not (a) use the legacy <c>AudioMagicBytesValidator</c>, nor (b) declare its own
    /// <c>*PayloadValidator</c> fork — the historical tidalarr <c>TidalDownloadPayloadValidator</c> +
    /// qobuz m4a-workaround divergence this guard exists to prevent regressing. Applicable only to
    /// download-client plugins (import-list plugins such as brainarr → N/A); source-scan based and
    /// conservative (no source root → skip). Note: inline MP4-box integrity checks on decrypted DASH
    /// segments (moov/mdat) are a DISTINCT concern (segment integrity, not file-level audio validation)
    /// and are intentionally NOT flagged — this targets file-level audio-payload validation only.
    /// </summary>
    public virtual ComplianceResult Check_DownloadClientUsesCommonPayloadValidator()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();
        if (!PluginDeclaresHostDownloadClient(assembly)) return ComplianceResult.Success; // N/A (no download client)
        if (!Directory.Exists(PluginSourceRoot)) return Skipped();

        var excluded = new[]
        {
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}ext{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            ".Tests" + Path.DirectorySeparatorChar,
            $"{Path.DirectorySeparatorChar}examples{Path.DirectorySeparatorChar}",
        };
        // (a) legacy weak validator usage; (b) a plugin-local *PayloadValidator fork declaration.
        var legacyUse = new System.Text.RegularExpressions.Regex(
            @"\bAudioMagicBytesValidator\b", System.Text.RegularExpressions.RegexOptions.Compiled);
        var forkClass = new System.Text.RegularExpressions.Regex(
            @"\bclass\s+[A-Za-z0-9_]*PayloadValidator\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(PluginSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (excluded.Any(x => file.Contains(x, StringComparison.Ordinal))) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            var rel = Path.GetRelativePath(RepoRootPath, file);
            if (legacyUse.IsMatch(text))
                offenders.Add($"{rel}: uses the legacy AudioMagicBytesValidator");
            if (forkClass.IsMatch(text))
                offenders.Add($"{rel}: declares a plugin-local *PayloadValidator fork");
        }

        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                "Audio-payload validation must use Lidarr.Plugin.Common.Utilities.DownloadPayloadValidator " +
                $"(the canonical superset), not the legacy AudioMagicBytesValidator or a plugin-local fork: {string.Join("; ", offenders)}");
    }

    #endregion

    #region Check_SimpleDownloadOrchestratorCoverArtComplianceAdopted

    /// <summary>
    /// Plugins that route downloads through <c>SimpleDownloadOrchestrator</c> must adopt
    /// <see cref="CoverArtEmbeddingComplianceTestBase"/> in their test suite. The orchestrator can
    /// only embed cover art when the plugin's download-path <c>StreamingAlbum</c> carries a fetchable
    /// cover URL; without a plugin fixture that drives the real mapper, this cross-plugin behavior can
    /// regress silently on a Common pin bump.
    /// </summary>
    public virtual ComplianceResult Check_SimpleDownloadOrchestratorCoverArtComplianceAdopted()
    {
        if (!Directory.Exists(PluginSourceRoot)) return Skipped();

        var simpleOrchestratorUse = new System.Text.RegularExpressions.Regex(
            @"\bnew\s+SimpleDownloadOrchestrator\b|:\s*(?:Lidarr\.Plugin\.Common\.Services\.Download\.)?SimpleDownloadOrchestrator\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var usageFiles = new List<string>();
        foreach (var file in EnumerateCSharpFiles(PluginSourceRoot, includeTests: false))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            if (simpleOrchestratorUse.IsMatch(text))
                usageFiles.Add(Path.GetRelativePath(RepoRootPath, file));
        }

        if (usageFiles.Count == 0) return ComplianceResult.Success; // N/A (does not use the cover-fetch seam)

        var testsRoot = Path.Combine(RepoRootPath, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return ComplianceResult.Failure(
                $"Plugin uses SimpleDownloadOrchestrator ({string.Join(", ", usageFiles)}) but has no tests/ directory adopting CoverArtEmbeddingComplianceTestBase.");
        }

        var complianceSubclass = new System.Text.RegularExpressions.Regex(
            @"\bclass\s+\w+(?:\s*<[^>]+>)?\s*:\s*(?:Lidarr\.Plugin\.Common\.TestKit\.Compliance\.)?CoverArtEmbeddingComplianceTestBase\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var file in EnumerateCSharpFiles(testsRoot, includeTests: true))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            if (complianceSubclass.IsMatch(text))
                return ComplianceResult.Success;
        }

        return ComplianceResult.Failure(
            "Plugin uses SimpleDownloadOrchestrator but does not adopt CoverArtEmbeddingComplianceTestBase. " +
            "Add a parity/compliance test that builds the real download-path StreamingAlbum both with and " +
            "without provider cover art, so Common's cover-art embedding contract is guarded. " +
            $"SimpleDownloadOrchestrator usage: {string.Join(", ", usageFiles)}");
    }

    private IEnumerable<string> EnumerateCSharpFiles(string root, bool includeTests)
    {
        var excluded = new[]
        {
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}ext{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}examples{Path.DirectorySeparatorChar}",
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (excluded.Any(x => file.Contains(x, StringComparison.Ordinal))) continue;
            if (!includeTests &&
                (file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 file.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 file.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                continue;
            }

            yield return file;
        }
    }

    #endregion

    #region Check_FileClassNameParity

    /// <summary>
    /// Single-type C# files should be named after the public type they declare (parity-matrix
    /// row 30: file ↔ class name parity). Flags a <c>.cs</c> file under <see cref="PluginSourceRoot"/>
    /// that declares EXACTLY ONE public top-level type whose name (generic arity stripped) differs
    /// from the file name. Conservative to avoid false positives: multi-type grouping files (DTO
    /// bundles, exception families, interface+impl pairs) declare more than one public type and are
    /// skipped; partial types and dotted/generated file names (<c>X.Designer.cs</c>, <c>X.g.cs</c>,
    /// partial-split files) are skipped. All four plugins are currently clean (row 30 ✓); this guard
    /// turns that into drift-prevention so a future copy-pasted mis-named file fails CI.
    /// </summary>
    public virtual ComplianceResult Check_FileClassNameParity()
    {
        if (!Directory.Exists(PluginSourceRoot)) return Skipped();

        var excluded = new[]
        {
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}ext{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        };
        // Public top-level (or nested — counted, which makes multi-type files exceed 1 and skip) type.
        var typeDecl = new System.Text.RegularExpressions.Regex(
            @"(?m)^\s*(?:\[[^\]]*\]\s*)*public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|unsafe\s+)*(?:class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var partialDecl = new System.Text.RegularExpressions.Regex(
            @"(?m)^\s*(?:\[[^\]]*\]\s*)*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|unsafe\s+)*partial\s+(?:class|interface|record|struct)\s+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(PluginSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (excluded.Any(x => file.Contains(x, StringComparison.Ordinal))) continue;
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.Contains('.')) continue; // dotted (partial splits / *.Designer / *.g) — skip
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (partialDecl.IsMatch(text)) continue; // partial type spans files — name needn't match
            var matches = typeDecl.Matches(text);
            if (matches.Count != 1) continue; // 0 = no public type; >1 = grouping file (allowed)
            var typeName = matches[0].Groups[1].Value;
            if (string.Equals(typeName, fileName, StringComparison.Ordinal)) continue;

            // The single PUBLIC type's name differs from the file name — but if the file's TOP-LEVEL
            // type (any accessibility) is itself named after the file, the file IS correctly named and
            // the public type is a nested helper (e.g. an `internal *HealthDiagnostics` whose only
            // public member is a nested `Capabilities`). Only flag when no top-level type matches the
            // file name. (^-anchored = top-level in the file-scoped-namespace style these repos use.)
            var declaresFileNameTopLevelType = System.Text.RegularExpressions.Regex.IsMatch(
                text,
                $@"(?m)^(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|unsafe)\s+)*(?:class|interface|record|struct|enum)\s+{System.Text.RegularExpressions.Regex.Escape(fileName)}\b");
            if (declaresFileNameTopLevelType) continue;

            offenders.Add($"{Path.GetRelativePath(RepoRootPath, file)} (file '{fileName}' != type '{typeName}')");
        }
        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Single-type files must be named after their public type (file↔class parity): {string.Join("; ", offenders)}");
    }

    #endregion

    #region Check_ClaudeMdDocumentsCommonHelpers

    /// <summary>
    /// Each plugin's <c>CLAUDE.md</c> must carry a "Common helpers in use" section (parity-matrix
    /// row 29) — the human-readable index of which shared Common helpers the plugin adopts, kept
    /// next to the code so reviewers can spot a plugin that quietly hand-rolls instead of adopting.
    /// Presence-only check (the content is reviewed by humans); repos without a CLAUDE.md are skipped.
    /// </summary>
    public virtual ComplianceResult Check_ClaudeMdDocumentsCommonHelpers()
    {
        var path = Path.Combine(RepoRootPath, "CLAUDE.md");
        if (!File.Exists(path)) return Skipped();
        string text;
        try { text = File.ReadAllText(path); }
        catch { return Skipped(); }
        return text.Contains("Common helpers in use", StringComparison.OrdinalIgnoreCase)
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                "CLAUDE.md is missing the 'Common helpers in use' section — document which Lidarr.Plugin.Common helpers the plugin adopts so reviewers can catch hand-rolled drift.");
    }

    #endregion

    #region Check_DownloadClientStampsRegisteredClientId

    /// <summary>
    /// A plugin's download client must stamp every reported <c>DownloadClientItem</c> with the
    /// registered client id — <c>DownloadClientInfo.Id == this client's Definition.Id</c> — never a
    /// literal <c>0</c>. Lidarr's <c>DownloadMonitoringService</c> → <c>CompletedDownloadService</c> →
    /// <c>DownloadClientProvider.Get(DownloadClientInfo.Id)</c> resolves the owning client by that id;
    /// a wrong/zero id makes its <c>.Single(...)</c> throw "Sequence contains no matching element", so
    /// every completed download wedges at "Couldn't process tracked download" and never imports. (Live
    /// qobuz regression, 2026-05-31: <c>GetItems()</c> passed <c>0</c> → every download stuck.)
    /// <para>
    /// Canonical shape (tidal/apple/amazon): <c>DownloadClientItemClientInfo.FromDownloadClient(this, …)</c>,
    /// the host helper that always reads <c>this.Definition.Id/Name</c>. qobuz derives the id explicitly
    /// from <c>Definition?.Id</c>. The guard accepts either; it fails a <c>GetItems()</c> that neither
    /// uses <c>FromDownloadClient(this, …)</c> nor references <c>Definition.Id/Name</c>, or that passes a
    /// literal <c>0</c> to a <c>ToDownloadClientItem(...)</c> converter.
    /// </para>
    /// <para>
    /// Applicable only to plugins that ship a host-contract download client (import-list plugins such as
    /// brainarr → N/A). Source-scan based (like <see cref="Check_NoFluentValidation_ErrorsApi_Drift"/>):
    /// it isolates each download client's <c>GetItems()</c> body and is conservative — if no body can be
    /// located it passes rather than false-fail.
    /// </para>
    /// </summary>
    public virtual ComplianceResult Check_DownloadClientStampsRegisteredClientId()
    {
        var assembly = PluginAssembly;
        if (assembly == null) return Skipped();
        if (!PluginDeclaresHostDownloadClient(assembly)) return ComplianceResult.Success; // N/A (no download client)
        if (!Directory.Exists(PluginSourceRoot)) return Skipped();

        var excluded = new[]
        {
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}ext{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            ".Tests" + Path.DirectorySeparatorChar,
            $"{Path.DirectorySeparatorChar}examples{Path.DirectorySeparatorChar}",
        };

        // Host-contract download clients subclass DownloadClientBase<TSettings> and override GetItems().
        var clientBasePattern = new System.Text.RegularExpressions.Regex(
            @":\s*DownloadClientBase\s*<", System.Text.RegularExpressions.RegexOptions.Compiled);
        var derivesFromDefinition = new System.Text.RegularExpressions.Regex(
            @"FromDownloadClient\s*\(\s*this\b|Definition\s*\??\s*\.\s*(Id|Name)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var stampsLiteralZero = new System.Text.RegularExpressions.Regex(
            @"ToDownloadClientItem\s*\(\s*0\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        var offenders = new List<string>();
        var scannedAny = false;
        foreach (var file in Directory.EnumerateFiles(PluginSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (excluded.Any(x => file.Contains(x, StringComparison.Ordinal))) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (!clientBasePattern.IsMatch(text)) continue;
            if (!text.Contains("GetItems", StringComparison.Ordinal)) continue;

            var body = ExtractMethodBody(text, "GetItems");
            if (body == null) continue; // couldn't isolate the body — don't false-fail
            scannedAny = true;

            var derives = derivesFromDefinition.IsMatch(body);
            var stampsZero = stampsLiteralZero.IsMatch(body);
            if (!derives || stampsZero)
            {
                offenders.Add(
                    $"{Path.GetRelativePath(RepoRootPath, file)}: GetItems() must stamp DownloadClientInfo.Id " +
                    "from this client's Definition (use DownloadClientItemClientInfo.FromDownloadClient(this, …) " +
                    "or pass Definition.Id) — a literal 0 makes Lidarr's CompletedDownloadService fail to resolve " +
                    "the owning client and wedges every completed download.");
            }
        }

        // No locatable download-client GetItems() body to assert against — treat as N/A rather than fail.
        if (!scannedAny) return ComplianceResult.Success;

        return offenders.Count == 0
            ? ComplianceResult.Success
            : ComplianceResult.Failure(
                $"Download client(s) must report DownloadClientInfo.Id == Definition.Id (download-client-id contract): {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// True if the plugin assembly declares a host-contract download client — a concrete type
    /// implementing the abstractions <c>IDownloadClient</c> or subclassing Lidarr's
    /// <c>DownloadClientBase&lt;T&gt;</c>. Import-list plugins (e.g. brainarr) declare neither.
    /// </summary>
    private bool PluginDeclaresHostDownloadClient(Assembly assembly)
    {
        const string dcIface = "Lidarr.Plugin.Abstractions.Contracts.IDownloadClient";
        return SafeGetTypes(assembly).Any(t =>
        {
            if (t == null) return false;
            try
            {
                if (t.IsInterface || t.IsAbstract) return false;
                if (t.GetInterfaces().Any(i => i.FullName == dcIface)) return true;
                var b = t.BaseType;
                while (b != null)
                {
                    if ((b.Name ?? string.Empty).StartsWith("DownloadClientBase", StringComparison.Ordinal)) return true;
                    b = b.BaseType;
                }
                return false;
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// Extracts the body text (the enclosing <c>{ … }</c>, or the <c>=&gt; … ;</c> for an expression
    /// body) of the first method named <paramref name="methodName"/> in <paramref name="source"/>.
    /// Returns null if the method or a balanced body can't be located (callers treat null as "skip").
    /// Brace-matching tolerates balanced interpolation braces (<c>$"{x}"</c>); a stray unbalanced brace
    /// inside a string/comment yields null (safe — the caller skips rather than false-fails).
    /// </summary>
    private static string? ExtractMethodBody(string source, string methodName)
    {
        var searchFrom = 0;
        while (true)
        {
            var nameIdx = source.IndexOf(methodName, searchFrom, StringComparison.Ordinal);
            if (nameIdx < 0) return null;
            var afterName = nameIdx + methodName.Length;
            // Require a left word boundary so "GetItems" doesn't match inside "FooGetItems".
            if (nameIdx > 0)
            {
                var prev = source[nameIdx - 1];
                if (char.IsLetterOrDigit(prev) || prev == '_') { searchFrom = afterName; continue; }
            }
            // Next non-space char must open a parameter list.
            var p = afterName;
            while (p < source.Length && char.IsWhiteSpace(source[p])) p++;
            if (p >= source.Length || source[p] != '(') { searchFrom = afterName; continue; }
            // Match the parameter-list parens.
            var parenDepth = 0;
            var q = p;
            for (; q < source.Length; q++)
            {
                if (source[q] == '(') parenDepth++;
                else if (source[q] == ')') { parenDepth--; if (parenDepth == 0) { q++; break; } }
            }
            if (q >= source.Length) return null;
            var r = q;
            while (r < source.Length && char.IsWhiteSpace(source[r])) r++;
            if (r >= source.Length) return null;
            if (source[r] == '{')
            {
                var depth = 0;
                for (var i = r; i < source.Length; i++)
                {
                    if (source[i] == '{') depth++;
                    else if (source[i] == '}') { depth--; if (depth == 0) return source.Substring(r, i - r + 1); }
                }
                return null; // unbalanced
            }
            if (source[r] == '=' && r + 1 < source.Length && source[r + 1] == '>')
            {
                var semi = source.IndexOf(';', r);
                return semi < 0 ? null : source.Substring(r, semi - r + 1);
            }
            // Declaration without an inline body (interface/abstract) or a generic constraint — keep looking.
            searchFrom = afterName;
        }
    }

    #endregion

    #region Aggregator

    #region Check_EnforcesAlbumCompletionPolicy

    /// <summary>
    /// Every streaming plugin must share one album-completion rule: an incomplete album (any
    /// track missing) is NOT a successful download, so it is reported Failed (Lidarr blocklists
    /// + re-searches / falls back) rather than Completed (which Lidarr permanently rejects as
    /// "Has missing tracks", silently wasting the downloaded files). The rule lives in
    /// <see cref="AlbumCompletionPolicy"/>; this guard pins it in every plugin's pinned Common,
    /// so a plugin that delegates to it provably inherits the fix and cannot regress to
    /// "partial album == success". (Was a live qobuz regression: Aphex Twin – Drukqs, 29/30.)
    /// Unlike the other behavior checks this needs no <see cref="PluginAssembly"/> — it asserts
    /// the shared rule directly, so it runs for every plugin regardless of opt-in.
    /// </summary>
    public virtual ComplianceResult Check_EnforcesAlbumCompletionPolicy()
    {
        var errors = new List<string>();

        // Incomplete must never be successful — not even above the success-rate threshold.
        if (AlbumCompletionPolicy.IsAlbumDownloadSuccessful(totalTracks: 30, successfulTracks: 29))
            errors.Add("AlbumCompletionPolicy treats 29/30 as successful; an incomplete album must report Failed.");
        if (AlbumCompletionPolicy.IsAlbumDownloadSuccessful(totalTracks: 10, successfulTracks: 8))
            errors.Add("AlbumCompletionPolicy treats 8/10 as successful; an incomplete album must report Failed.");
        // A complete album must be successful.
        if (!AlbumCompletionPolicy.IsAlbumDownloadSuccessful(totalTracks: 10, successfulTracks: 10))
            errors.Add("AlbumCompletionPolicy treats a complete 10/10 album as unsuccessful.");

        return errors.Count == 0 ? ComplianceResult.Success : new ComplianceResult(false, errors);
    }

    #endregion

    /// <summary>
    /// Runs the behavior-contract checks. Returns a separate report so callers can decide
    /// whether to combine with structural results.
    /// </summary>
    public virtual ComplianceReport RunBehaviorContractChecks()
    {
        var results = new Dictionary<string, ComplianceResult>
        {
            [nameof(Check_EnforcesAlbumCompletionPolicy)] = Check_EnforcesAlbumCompletionPolicy(),
            [nameof(Check_UsesCommonFileTokenStore)] = Check_UsesCommonFileTokenStore(),
            [nameof(Check_UsesCommonHttpResponseCache)] = Check_UsesCommonHttpResponseCache(),
            [nameof(Check_RegistersBridgeDefaults)] = Check_RegistersBridgeDefaults(),
            [nameof(Check_PluginManifest_Capabilities_HaveBackingTypes)] = Check_PluginManifest_Capabilities_HaveBackingTypes(),
            [nameof(Check_NoFluentValidation_ErrorsApi_Drift)] = Check_NoFluentValidation_ErrorsApi_Drift(),
            [nameof(Check_UsesCommonPluginConfigRoots)] = Check_UsesCommonPluginConfigRoots(),
            [nameof(Check_UsesCommonLyricsEnricher)] = Check_UsesCommonLyricsEnricher(),
            [nameof(Check_UsesCommonDiagnosticTypes)] = Check_UsesCommonDiagnosticTypes(),
            [nameof(Check_UsesCommonDownloadTelemetrySink)] = Check_UsesCommonDownloadTelemetrySink(),
            [nameof(Check_DownloadClientUsesPathTraversalGuard)] = Check_DownloadClientUsesPathTraversalGuard(),
            [nameof(Check_DownloadClientStampsRegisteredClientId)] = Check_DownloadClientStampsRegisteredClientId(),
            [nameof(Check_DownloadClientUsesCommonPayloadValidator)] = Check_DownloadClientUsesCommonPayloadValidator(),
            [nameof(Check_SimpleDownloadOrchestratorCoverArtComplianceAdopted)] = Check_SimpleDownloadOrchestratorCoverArtComplianceAdopted(),
            [nameof(Check_FileClassNameParity)] = Check_FileClassNameParity(),
            [nameof(Check_ClaudeMdDocumentsCommonHelpers)] = Check_ClaudeMdDocumentsCommonHelpers(),
        };

        var passed = results.Values.Count(r => r.Passed);
        return new ComplianceReport(results, passed, results.Count);
    }

    #endregion
}
