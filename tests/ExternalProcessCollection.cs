using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Serializes the integration tests that shell out to external <c>pwsh</c> and
    /// <c>dotnet</c> (build/msbuild) processes into a single non-parallel collection.
    ///
    /// With xUnit's default collection parallelism (maxParallelThreads = 4), several of
    /// these heavyweight classes could run at once, each spawning a multi-core MSBuild /
    /// VBCSCompiler tree. That oversubscribed the CI VM badly enough to starve even the
    /// lightweight pwsh-only cases past their per-process timeouts, producing flaky
    /// "Process timed out after 60s" failures and the occasional test-host crash on
    /// Windows CI. Running them one-at-a-time removes the contention while leaving the
    /// fast unit-test suite fully parallel.
    /// </summary>
    [CollectionDefinition("ExternalProcess", DisableParallelization = true)]
    public sealed class ExternalProcessCollection
    {
    }
}
