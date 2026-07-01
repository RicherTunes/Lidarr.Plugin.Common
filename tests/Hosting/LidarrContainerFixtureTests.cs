using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.TestKit.Hosting;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="LidarrContainerFixture"/> and
/// <see cref="LidarrContainerOptions"/> that do not actually start Docker.
/// Wave 22a — guards the lifted fixture's surface (skip-when-no-Docker, options
/// validation, plugin DLL discovery wiring).
/// </summary>
public sealed class LidarrContainerFixtureTests
{
    private static LidarrContainerOptions BuildOptions(
        Func<string, string?>? findDll = null,
        string containerName = "test-e2e",
        int port = 18686)
        => new(
            DockerImage: "ghcr.io/hotio/lidarr:nightly-3.1.3.4970",
            ContainerName: containerName,
            LidarrPort: port,
            PluginMountPath: "/config/plugins/Test/Plugin",
            PluginDllFileName: "Lidarr.Plugin.Test.dll",
            FindPluginDll: findDll ?? (_ => null),
            PluginEntrySubstring: "Test");

    // -- Options validation ---------------------------------------------

    [Fact]
    public void Validate_AcceptsWellFormedOptions()
    {
        var opts = BuildOptions();
        opts.Validate(); // does not throw
    }

    [Fact]
    public void Validate_ThrowsWhenDockerImageEmpty()
    {
        var opts = BuildOptions() with { DockerImage = "" };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_ThrowsWhenContainerNameEmpty()
    {
        var opts = BuildOptions() with { ContainerName = "" };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_ThrowsWhenPortOutOfRange()
    {
        var opts = BuildOptions() with { LidarrPort = 0 };
        Assert.Throws<ArgumentException>(() => opts.Validate());

        var opts2 = BuildOptions() with { LidarrPort = 70_000 };
        Assert.Throws<ArgumentException>(() => opts2.Validate());
    }

    [Fact]
    public void Validate_ThrowsWhenMountPathEmpty()
    {
        var opts = BuildOptions() with { PluginMountPath = "  " };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_ThrowsWhenDllFileNameEmpty()
    {
        var opts = BuildOptions() with { PluginDllFileName = "" };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_ThrowsWhenEntrySubstringEmpty()
    {
        var opts = BuildOptions() with { PluginEntrySubstring = "" };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    [Fact]
    public void Validate_ThrowsWhenStartupTimeoutNonPositive()
    {
        var opts = BuildOptions() with { StartupTimeoutSeconds = 0 };
        Assert.Throws<ArgumentException>(() => opts.Validate());
    }

    // -- Constructor wiring ---------------------------------------------

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LidarrContainerFixture(null!));
    }

    [Fact]
    public void Constructor_RecordOptionsAreExposed()
    {
        var opts = BuildOptions(containerName: "expected-name", port: 18999);
        using var fixture = new TestableContainerFixture(opts, dockerAvailable: false);
        Assert.Equal("expected-name", fixture.Options.ContainerName);
        Assert.Equal(18999, fixture.Options.LidarrPort);
        Assert.Equal("http://localhost:18999", fixture.BaseUrl);
    }

    // -- Skip behavior --------------------------------------------------

    [Fact]
    public async Task InitializeAsync_SetsSkipReason_WhenDockerUnavailable()
    {
        bool findDllCalled = false;
        var opts = BuildOptions(findDll: _ =>
        {
            findDllCalled = true;
            return null;
        });

        using var fixture = new TestableContainerFixture(opts, dockerAvailable: false);
        await fixture.InitializeAsync();

        Assert.NotNull(fixture.SkipReason);
        Assert.Contains("Docker", fixture.SkipReason!, StringComparison.OrdinalIgnoreCase);
        // FindPluginDll should NOT be invoked when Docker is unavailable
        Assert.False(findDllCalled, "FindPluginDll must not be called when Docker is unavailable.");
    }

    [Fact]
    public async Task InitializeAsync_SetsSkipReason_WhenPluginDllMissing()
    {
        bool findDllCalled = false;
        var opts = BuildOptions(findDll: _ =>
        {
            findDllCalled = true;
            return null; // simulate "not built"
        });

        using var fixture = new TestableContainerFixture(opts, dockerAvailable: true);
        await fixture.InitializeAsync();

        Assert.True(findDllCalled, "FindPluginDll should be called when Docker is available.");
        Assert.NotNull(fixture.SkipReason);
        Assert.Contains("Lidarr.Plugin.Test.dll", fixture.SkipReason!);
    }

    [Fact]
    public async Task InitializeAsync_SetsSkipReason_WhenPluginRefsAbstractionsButSidecarMissing()
    {
        // Build a real plugin DLL that has an AssemblyRef to Lidarr.Plugin.Abstractions
        // (i.e., NOT ILRepack-merged) but DOESN'T ship the sidecar — that's an unloadable
        // dev mistake the fixture must skip on.
        string tempDir = Path.Combine(Path.GetTempPath(), "lpc-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dllPath = Path.Combine(tempDir, "Lidarr.Plugin.Test.dll");
        // Use this test assembly itself — it references Lidarr.Plugin.Abstractions transitively.
        File.Copy(typeof(LidarrContainerFixtureTests).Assembly.Location, dllPath, overwrite: true);

        try
        {
            var opts = BuildOptions(findDll: _ => dllPath);
            using var fixture = new TestableContainerFixture(opts, dockerAvailable: true);
            await fixture.InitializeAsync();

            Assert.NotNull(fixture.SkipReason);
            Assert.Contains("host-bridge", fixture.SkipReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InitializeAsync_AcceptsMergedBuild_WithoutAbstractionsSidecar()
    {
        // Post PR #485: merged plugin DLLs have Abstractions ILRepacked + internalized.
        // No external AssemblyRef to Abstractions, no sidecar. The fixture MUST accept
        // this shape — otherwise every E2E test silently skips post-rollout (the C1 risk
        // the adversarial review caught).
        string tempDir = Path.Combine(Path.GetTempPath(), "lpc-fixture-merged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dllPath = Path.Combine(tempDir, "Lidarr.Plugin.Test.dll");

        // Generate a minimal valid PE+managed assembly with no Abstractions AssemblyRef
        // by emitting one via System.Reflection.Emit + saving to disk.
        var an = new System.Reflection.AssemblyName("Lidarr.Plugin.Test");
        var asmBuilder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(an, System.Reflection.Emit.AssemblyBuilderAccess.Run);
        // Emit minimal IL — but since we can't save dynamic assemblies on .NET 8 without
        // the SaveAssembly NuGet, fall back to writing a tiny but valid empty PE manifest.
        // Even simpler: copy any small DLL from .NET runtime that has zero Abstractions ref
        // (System.Runtime.dll qualifies).
        var sysRuntime = typeof(object).Assembly.Location;
        File.Copy(sysRuntime, dllPath, overwrite: true);

        try
        {
            var opts = BuildOptions(findDll: _ => dllPath);
            using var fixture = new TestableContainerFixture(opts, dockerAvailable: true);
            // Stub docker so we don't actually try to run a container — we only care that
            // IsHostBridgeBuild accepts the merged shape (no skip due to sidecar absence).
            await fixture.InitializeAsync();

            // Either no skip (full pass) or a skip that's NOT about host-bridge. The
            // critical assertion is that the missing sidecar alone doesn't trigger skip.
            if (fixture.SkipReason is { } reason)
            {
                Assert.DoesNotContain("host-bridge", reason, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow_WhenContainerNeverStarted()
    {
        var opts = BuildOptions();
        using var fixture = new TestableContainerFixture(opts, dockerAvailable: false);
        await fixture.InitializeAsync();
        await fixture.DisposeAsync();
    }

    /// <summary>
    /// Subclass that overrides <c>DockerAvailable</c> so unit tests can drive
    /// the skip-path decisions without invoking the real <c>docker</c> binary.
    /// </summary>
    private sealed class TestableContainerFixture : LidarrContainerFixture, IDisposable
    {
        private readonly bool _dockerAvailable;

        public TestableContainerFixture(LidarrContainerOptions options, bool dockerAvailable)
            : base(options)
        {
            _dockerAvailable = dockerAvailable;
        }

        protected override bool DockerAvailable() => _dockerAvailable;

        public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
    }
}
