using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;

// Simulates an ILRepack-internalized common token store: the host assembly is the
// plugin's (in this case, the test assembly), but the type's Namespace is still under
// Lidarr.Plugin.Common.*. Used by EcosystemParityTestBaseExtensionTests to verify the
// namespace allowlist refinement.
namespace Lidarr.Plugin.Common.Internalized.TokenStores;

internal sealed class FakeInternalizedTokenStore : ITokenStore<string>
{
    public Task<TokenEnvelope<string>?> LoadAsync(CancellationToken ct = default) => Task.FromResult<TokenEnvelope<string>?>(null);
    public Task SaveAsync(TokenEnvelope<string> envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeInternalizedResponseCache : IStreamingResponseCache
{
    public T? Get<T>(string endpoint, System.Collections.Generic.Dictionary<string, string> parameters) where T : class => null;
    public void Set<T>(string endpoint, System.Collections.Generic.Dictionary<string, string> parameters, T value) where T : class { }
    public void Set<T>(string endpoint, System.Collections.Generic.Dictionary<string, string> parameters, T value, System.TimeSpan duration) where T : class { }
    public bool ShouldCache(string endpoint) => false;
    public System.TimeSpan GetCacheDuration(string endpoint) => System.TimeSpan.Zero;
    public string GenerateCacheKey(string endpoint, System.Collections.Generic.Dictionary<string, string> parameters) => endpoint;
    public void Clear() { }
    public void ClearEndpoint(string endpoint) { }
}
