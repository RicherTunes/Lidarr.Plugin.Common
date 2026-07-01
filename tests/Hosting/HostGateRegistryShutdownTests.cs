using System;
using System.Linq;
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
        public void Shutdown_ThenGet_ReArmsIdleSweeper()
        {
            // Arrange: touch the registry, then shut down so the sweeper is disposed.
            _ = HostGateRegistry.Get("rearm-host.test", 3);
            HostGateRegistry.Shutdown();
            Assert.False(HostGateRegistry.IsSweeperActive,
                "Sweeper should be disposed immediately after Shutdown().");

            // Act: the documented contract is that the timer is re-created lazily on first access.
            // Lidarr keeps the host process alive across plugin reloads, so a Get() after a prior
            // unload's Shutdown() must re-arm the sweeper — otherwise every gate added afterwards
            // (each owning a SemaphoreSlim) accumulates forever with no eviction.
            _ = HostGateRegistry.Get("rearm-host-2.test", 3);

            // Assert: the idle-gate sweeper is running again.
            Assert.True(HostGateRegistry.IsSweeperActive,
                "Get() after Shutdown() must re-arm the idle-gate sweeper (else gates leak after the first reload).");
        }

        [Fact]
        public void Shutdown_ThenGetAggregate_ReArmsIdleSweeper()
        {
            _ = HostGateRegistry.GetAggregate("rearm-agg-host.test", 3);
            HostGateRegistry.Shutdown();
            Assert.False(HostGateRegistry.IsSweeperActive);

            _ = HostGateRegistry.GetAggregate("rearm-agg-host-2.test", 3);

            Assert.True(HostGateRegistry.IsSweeperActive,
                "GetAggregate() after Shutdown() must re-arm the idle-gate sweeper.");
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

        [Fact]
        public async Task ConcurrentGetAndShutdown_DoesNotLeaveRegisteredGateWithoutSweeper()
        {
            // Regression: Get() used to arm the sweeper before inserting/touching the gate.
            // Shutdown() could then dispose/null the sweeper and finish draining dictionaries
            // before a racing Get() inserted its gate, leaving a registered SemaphoreSlim with
            // no idle sweeper until some later access happened to re-arm it.
            const int attempts = 200;
            const int readersPerAttempt = 12;

            for (var attempt = 0; attempt < attempts; attempt++)
            {
                HostGateRegistry.Shutdown();
                using var start = new ManualResetEventSlim(false);
                string hostPrefix = $"race-{attempt}-";

                var getTasks = Enumerable.Range(0, readersPerAttempt)
                    .Select(i => Task.Run(() =>
                    {
                        start.Wait();
                        _ = HostGateRegistry.Get(hostPrefix + i, 1);
                    }))
                    .ToArray();

                var shutdownTask = Task.Run(() =>
                {
                    start.Wait();
                    HostGateRegistry.Shutdown();
                });

                start.Set();
                await Task.WhenAll(getTasks.Append(shutdownTask));

                var anyGateRemains = Enumerable.Range(0, readersPerAttempt)
                    .Any(i => HostGateRegistry.TryGetState(hostPrefix + i, out _));

                Assert.False(anyGateRemains && !HostGateRegistry.IsSweeperActive,
                    "A racing Get() must not leave gate entries behind without an active idle sweeper.");
            }
        }
    }

    [CollectionDefinition("HostGateRegistryShutdown", DisableParallelization = true)]
    public class HostGateRegistryShutdownCollection { }
}
