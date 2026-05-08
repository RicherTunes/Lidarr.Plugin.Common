using System.Diagnostics.CodeAnalysis;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Public companion to the internal IResiliencePolicyProvider. Plugins implement
    /// this interface to expose named resilience profiles ("auth", "search",
    /// "details", "catalog", "library", "download", "default") to the host.
    /// </summary>
    /// <remarks>
    /// The cross-plugin idiom (originally from applemusicarr) is to register a
    /// <c>StaticResiliencePolicyProvider</c> as the default fallback and a
    /// <see cref="FileResiliencePolicyProvider"/> wrapping it when a JSON file
    /// is configured for hot-reload tuning.
    /// </remarks>
    public interface IResilienceSettingsProvider
    {
        /// <summary>Resolves the resilience profile for the given name. Falls back to "default" when unknown.</summary>
        [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
            Justification = "Conventional provider method; widely used across plugin ecosystem.")]
        ResilienceProfileSettings Get(string profileName);
    }
}
