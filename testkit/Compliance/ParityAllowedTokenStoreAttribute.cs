using System;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Marks a plugin-local <c>ITokenStore&lt;T&gt;</c> implementation as a deliberate,
/// non-fork pattern (e.g. a fail-fast / no-op fallback used when the host has no
/// writable config path). When applied, <see cref="EcosystemParityTestBase.Check_UsesCommonFileTokenStore"/>
/// will accept the type instead of treating it as a forbidden fork.
/// </summary>
/// <remarks>
/// Use sparingly. The <see cref="Rationale"/> string is required and should describe why
/// the plugin cannot delegate to <c>FileTokenStore&lt;T&gt;</c> (e.g. "fail-fast no-op
/// for environments without a writable ConfigPath").
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ParityAllowedTokenStoreAttribute : Attribute
{
    /// <summary>Initializes the attribute with the required rationale.</summary>
    /// <param name="rationale">Human-readable explanation of why this type is allowed.</param>
    public ParityAllowedTokenStoreAttribute(string rationale)
    {
        Rationale = rationale ?? throw new ArgumentNullException(nameof(rationale));
    }

    /// <summary>Reason this plugin-local token store is permitted.</summary>
    public string Rationale { get; }
}
