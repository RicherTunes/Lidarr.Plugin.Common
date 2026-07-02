using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Hosting
{
    /// <summary>
    /// Static registry for plugin teardown hooks. Plugin module <c>Dispose()</c> methods call
    /// <see cref="Shutdown"/> once; each registered action is invoked in reverse-registration
    /// order (LIFO) so inner resources unwind before outer ones.
    ///
    /// <para>
    /// Why static? The consumers (e.g. MetricsCollector, SharedSystemHttpClient) are themselves
    /// static classes. Wrapping them in an instance interface would require changes throughout
    /// every consumer. The static registry is the simplest forwarder and matches the existing
    /// pattern in <see cref="Utilities.HostGateRegistry"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Thread-safe. All mutations are guarded by <c>_lock</c>.
    /// Shutdown is idempotent for a given set of registered hooks: it drains and clears the hooks,
    /// then re-arms the registry so a subsequent plugin lifecycle in the same process can re-register.
    /// Lidarr keeps the host process alive across plugin reloads, so the registry must NOT latch
    /// permanently after the first <see cref="Shutdown"/> — mirrors <see cref="Utilities.HostGateRegistry"/>'s
    /// re-arm philosophy.
    /// </remarks>
    public static class PluginLifecycle
    {
        private static readonly object _lock = new();
        private static readonly List<(string Name, Action Action)> _hooks = new();
        private static volatile bool _shuttingDown = false;
        private static ILogger? _logger;

        /// <summary>
        /// Attaches an optional logger. Failures in individual shutdown hooks will be logged
        /// at <c>Warning</c> level rather than silently swallowed.
        /// </summary>
        public static void AttachLogger(ILogger logger) => _logger = logger;

        /// <summary>
        /// Registers a shutdown action under the given name. Actions are invoked in LIFO order
        /// by <see cref="Shutdown"/>. Registrations made while a <see cref="Shutdown"/> is actively
        /// draining its already-snapshotted hooks are retained for the next shutdown.
        /// </summary>
        /// <param name="name">Human-readable name used in log messages on failure.</param>
        /// <param name="shutdown">Action to invoke on plugin teardown.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="name"/> or <paramref name="shutdown"/> is null.</exception>
        public static void RegisterShutdown(string name, Action shutdown)
        {
            if (name is null) throw new ArgumentNullException(nameof(name));
            if (shutdown is null) throw new ArgumentNullException(nameof(shutdown));

            lock (_lock)
            {
                // If a shutdown is actively draining, it has already taken and cleared its snapshot
                // under this same lock. New registrations therefore belong to the next lifecycle and
                // must be retained, not dropped.
                _hooks.Add((name, shutdown));
            }
        }

        /// <summary>
        /// Invokes all registered shutdown actions in reverse-registration order (LIFO), then
        /// clears them and re-arms the registry. A subsequent call with no new registrations drains
        /// an empty list (effective no-op); a hook registered after a completed Shutdown is retained
        /// and runs on the next Shutdown. Exceptions thrown by individual hooks are caught and logged;
        /// they do not prevent subsequent hooks from running.
        /// </summary>
        public static void Shutdown()
        {
            List<(string Name, Action Action)> hooks;
            lock (_lock)
            {
                // A concurrent Shutdown is already draining; let it own this pass. Its drain plus
                // re-arm leaves the registry consistent, and re-entrancy here would double-run hooks.
                if (_shuttingDown) return;

                _shuttingDown = true;

                // Snapshot in LIFO order and clear, so this drain owns exactly these hooks. New
                // registrations that race the drain are retained in _hooks for the next shutdown.
                hooks = new List<(string Name, Action Action)>(_hooks);
                hooks.Reverse();
                _hooks.Clear();
            }

            try
            {
                foreach (var (name, action) in hooks)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        try { _logger?.LogWarning(ex, "PluginLifecycle: shutdown hook '{HookName}' threw an exception.", name); }
                        catch { /* logger itself must not block teardown */ }
                    }
                }
            }
            finally
            {
                // Re-arm: leave the registry re-usable so a fresh plugin lifecycle in the same host
                // process can re-register and run its own teardown hooks on the next Shutdown.
                // Mirrors HostGateRegistry's re-arm philosophy. Done in finally so a hook that throws
                // past the inner catch (e.g. a logger failure) still re-arms the registry.
                lock (_lock)
                {
                    _shuttingDown = false;
                }
            }
        }

        /// <summary>
        /// Resets the registry to its initial state. Intended for use in unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            lock (_lock)
            {
                _hooks.Clear();
                _shuttingDown = false;
                _logger = null;
            }
        }
    }
}
