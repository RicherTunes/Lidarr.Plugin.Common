using System;
using System.Collections.Generic;
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

        // ── Late registration after Shutdown ──────────────────────────────────────

        [Fact]
        public void RegisterAfterShutdown_IsSilentlyIgnored()
        {
            PluginLifecycle.Shutdown(); // shut down first (empty, idempotent)

            bool lateHookFired = false;
            // Late registration must not throw, and must not fire (Shutdown already ran).
            var ex = Record.Exception(() =>
                PluginLifecycle.RegisterShutdown("late", () => lateHookFired = true));

            Assert.Null(ex);

            // Second Shutdown is a no-op; the late hook must never fire.
            PluginLifecycle.Shutdown();
            Assert.False(lateHookFired);
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
