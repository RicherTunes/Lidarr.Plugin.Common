using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Hosting;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hosting
{
    /// <summary>
    /// Verifies the behaviour of <see cref="PluginLifecycle"/>:
    ///   1. Single hook fires on Shutdown.
    ///   2. Multiple hooks fire in LIFO (reverse-registration) order.
    ///   3. A hook that throws does not prevent subsequent hooks from running.
    ///   4. Shutdown is idempotent — a second call is a no-op.
    ///   5. ResetForTesting restores clean state so subsequent tests are isolated.
    /// </summary>
    [Collection("PluginLifecycle")]
    public class PluginLifecycleTests : IDisposable
    {
        public PluginLifecycleTests()
        {
            // Start every test from a clean slate.
            PluginLifecycle.ResetForTesting();
        }

        public void Dispose()
        {
            // Ensure clean state even if a test fails mid-way.
            PluginLifecycle.ResetForTesting();
        }

        // ── Single hook ────────────────────────────────────────────────────────────

        [Fact]
        public void SingleHook_FiresOnShutdown()
        {
            bool fired = false;
            PluginLifecycle.RegisterShutdown("test-single", () => fired = true);
            PluginLifecycle.Shutdown();
            Assert.True(fired);
        }

        // ── LIFO ordering ─────────────────────────────────────────────────────────

        [Fact]
        public void MultipleHooks_FireInLifoOrder()
        {
            var order = new List<string>();
            PluginLifecycle.RegisterShutdown("first",  () => order.Add("first"));
            PluginLifecycle.RegisterShutdown("second", () => order.Add("second"));
            PluginLifecycle.RegisterShutdown("third",  () => order.Add("third"));

            PluginLifecycle.Shutdown();

            // Expected LIFO: third, second, first.
            Assert.Equal(new[] { "third", "second", "first" }, order);
        }

        // ── Exception isolation ───────────────────────────────────────────────────

        [Fact]
        public void ThrowingHook_DoesNotBlockSubsequentHooks()
        {
            bool laterFired = false;
            PluginLifecycle.RegisterShutdown("ok-first",   () => laterFired = true);
            PluginLifecycle.RegisterShutdown("bad-second", () => throw new InvalidOperationException("boom"));

            // Must not throw from Shutdown itself.
            var ex = Record.Exception(() => PluginLifecycle.Shutdown());
            Assert.Null(ex);

            // The hook registered before the throwing one (runs after in LIFO) must still fire.
            Assert.True(laterFired);
        }

        // ── Idempotency ───────────────────────────────────────────────────────────

        [Fact]
        public void Shutdown_IsIdempotent_SecondCallIsNoOp()
        {
            int callCount = 0;
            PluginLifecycle.RegisterShutdown("counter", () => callCount++);

            PluginLifecycle.Shutdown();
            PluginLifecycle.Shutdown(); // second call — must be no-op

            Assert.Equal(1, callCount);
        }

        [Fact]
        public void Shutdown_Idempotent_DoesNotThrow()
        {
            PluginLifecycle.RegisterShutdown("noop", () => { });

            var ex = Record.Exception(() =>
            {
                PluginLifecycle.Shutdown();
                PluginLifecycle.Shutdown();
                PluginLifecycle.Shutdown();
            });

            Assert.Null(ex);
        }

        // ── ResetForTesting ───────────────────────────────────────────────────────

        [Fact]
        public void ResetForTesting_ClearsStateForNextTest()
        {
            // Register a hook and shut down.
            bool fired = false;
            PluginLifecycle.RegisterShutdown("flag", () => fired = true);
            PluginLifecycle.Shutdown();
            Assert.True(fired);

            // Reset so the next "test" starts clean.
            PluginLifecycle.ResetForTesting();
            fired = false;

            // After reset, registering a new hook and calling Shutdown should work normally.
            PluginLifecycle.RegisterShutdown("flag2", () => fired = true);
            PluginLifecycle.Shutdown();
            Assert.True(fired);
        }

        // ── Re-usable across reloads (Lidarr keeps the host process alive) ─────────

        [Fact]
        public void Shutdown_LeavesRegistryReusable_NextLifecycleHookRunsOnNextShutdown()
        {
            // Lidarr keeps the host process alive across plugin reloads. After the first unload's
            // Shutdown() the registry must re-arm so a SECOND plugin lifecycle can register and run
            // teardown hooks. Previously _shuttingDown latched true forever and late hooks were dropped.
            bool hookAFired = false;
            PluginLifecycle.RegisterShutdown("hookA", () => hookAFired = true);
            PluginLifecycle.Shutdown();
            Assert.True(hookAFired, "First lifecycle's hook must run on the first Shutdown.");

            // Second lifecycle in the same process registers a fresh hook AFTER the first Shutdown.
            bool hookBFired = false;
            PluginLifecycle.RegisterShutdown("hookB", () => hookBFired = true);
            PluginLifecycle.Shutdown();
            Assert.True(hookBFired, "A hook registered after a completed Shutdown must run on the NEXT Shutdown.");
        }

        [Fact]
        public void Shutdown_DoesNotReRunHooksFromAPriorCompletedShutdown()
        {
            // After a Shutdown drains and clears its hooks, a subsequent Shutdown with NO new
            // registrations must be a no-op — it must not re-run the previously-drained hook.
            int hookACount = 0;
            PluginLifecycle.RegisterShutdown("hookA", () => hookACount++);
            PluginLifecycle.Shutdown();
            Assert.Equal(1, hookACount);

            // No new registration: the next Shutdown drains an empty list.
            PluginLifecycle.Shutdown();
            Assert.Equal(1, hookACount);

            // Now register a fresh hook and Shutdown — only the new hook runs (hookA is not re-run).
            int hookBCount = 0;
            PluginLifecycle.RegisterShutdown("hookB", () => hookBCount++);
            PluginLifecycle.Shutdown();
            Assert.Equal(1, hookACount);
            Assert.Equal(1, hookBCount);
        }

        [Fact]
        public async Task RegisterDuringActiveShutdown_IsRetainedForNextShutdown()
        {
            using var hookEntered = new ManualResetEventSlim(false);
            using var releaseHook = new ManualResetEventSlim(false);

            int slowHookCount = 0;
            int nextLifecycleHookCount = 0;

            PluginLifecycle.RegisterShutdown("slow", () =>
            {
                hookEntered.Set();
                Assert.True(releaseHook.Wait(TimeSpan.FromSeconds(5)));
                slowHookCount++;
            });

            var firstShutdown = Task.Run(() => PluginLifecycle.Shutdown());
            Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(5)),
                "The first shutdown hook must be actively draining before registering the next lifecycle hook.");

            PluginLifecycle.RegisterShutdown("next-lifecycle", () => nextLifecycleHookCount++);

            releaseHook.Set();
            await firstShutdown;

            Assert.Equal(1, slowHookCount);
            Assert.Equal(0, nextLifecycleHookCount);

            PluginLifecycle.Shutdown();

            Assert.Equal(1, slowHookCount);
            Assert.Equal(1, nextLifecycleHookCount);
        }

        // ── Null-argument guards ──────────────────────────────────────────────────

        [Fact]
        public void RegisterShutdown_NullName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PluginLifecycle.RegisterShutdown(null!, () => { }));
        }

        [Fact]
        public void RegisterShutdown_NullAction_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PluginLifecycle.RegisterShutdown("valid-name", null!));
        }
    }

    [CollectionDefinition("PluginLifecycle", DisableParallelization = true)]
    public class PluginLifecycleCollection { }
}
