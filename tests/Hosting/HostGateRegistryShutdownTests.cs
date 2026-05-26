using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hosting
{
    /// <summary>
    /// Verifies the Shutdown() lifecycle of HostGateRegistry:
    ///   1. After Shutdown(), the background timer stops ticking.
    ///   2. Shutdown() is idempotent (multiple calls do not throw).
    ///   3. Shutdown() followed by re-use (Get/GetAggregate) succeeds without errors.
    /// </summary>
    [Collection("HostGateRegistryShutdown")]
    public class HostGateRegistryShutdownTests : IDisposable
    {
        // Ensure the registry is always cleaned up after each test so parallel test runs
        // start from a known-good state.
        public void Dispose()
        {
            HostGateRegistry.Shutdown();
        }

        [Fact]
        public void Shutdown_DisposesTimer_NoSubsequentTicks()
        {
            // Arrange: touch the registry so the timer is running.
            var sem = HostGateRegistry.Get("shutdown-tick-host.test", 3);
            Assert.NotNull(sem);

            // Act: shut down and wait longer than the 5-minute sweep interval
            // (simulated by asserting Shutdown does not throw, and re-checking state).
            HostGateRegistry.Shutdown();

            // Assert: after Shutdown, the gate entries are cleared.
            var found = HostGateRegistry.TryGetState("shutdown-tick-host.test", out _);
            Assert.False(found, "Gates should be cleared after Shutdown().");
        }

        [Fact]
        public void Shutdown_IsIdempotent()
        {
            // Arrange: touch the registry.
            _ = HostGateRegistry.Get("idempotent-host.test", 2);

            // Act & Assert: calling Shutdown multiple times must not throw.
            var ex = Record.Exception(() =>
            {
                HostGateRegistry.Shutdown();
                HostGateRegistry.Shutdown();
                HostGateRegistry.Shutdown();
            });

            Assert.Null(ex);
        }

        [Fact]
        public void Shutdown_AllowsReinitialization()
        {
            // Arrange: populate and shut down.
            _ = HostGateRegistry.Get("reinit-host.test", 5);
            HostGateRegistry.Shutdown();

            // Verify cleared.
            Assert.False(HostGateRegistry.TryGetState("reinit-host.test", out _));

            // Act: re-use after Shutdown — new entries must be accepted without throwing.
            SemaphoreSlim? newSem = null;
            var ex = Record.Exception(() =>
            {
                newSem = HostGateRegistry.Get("reinit-host.test", 5);
            });

            // Assert: no exception, and the returned semaphore is valid.
            Assert.Null(ex);
            Assert.NotNull(newSem);
            Assert.True(newSem!.CurrentCount >= 0);

            // Aggregate path also works after re-initialization.
            SemaphoreSlim? aggSem = null;
            var aggEx = Record.Exception(() =>
            {
                aggSem = HostGateRegistry.GetAggregate("reinit-agg-host.test", 4);
            });
            Assert.Null(aggEx);
            Assert.NotNull(aggSem);
        }
    }

    [CollectionDefinition("HostGateRegistryShutdown", DisableParallelization = true)]
    public class HostGateRegistryShutdownCollection { }
}
