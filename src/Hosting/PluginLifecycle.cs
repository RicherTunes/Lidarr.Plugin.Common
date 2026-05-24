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
    /// Shutdown is idempotent: subsequent calls after the first are no-ops.
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
        /// by <see cref="Shutdown"/>. Registrations after <see cref="Shutdown"/> has been called
        /// are silently ignored (the plugin is already being torn down).
        /// </summary>
        /// <param name="name">Human-readable name used in log messages on failure.</param>
        /// <param name="shutdown">Action to invoke on plugin teardown.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="name"/> or <paramref name="shutdown"/> is null.</exception>
        public static void RegisterShutdown(string name, Action shutdown)
        {
            if (name is null) throw new ArgumentNullException(nameof(name));
            if (shutdown is null) throw new ArgumentNullException(nameof(shutdown));

            // If already shutting down, silently discard late registrations.
            if (_shuttingDown) return;

            lock (_lock)
            {
                if (_shuttingDown) return;
                _hooks.Add((name, shutdown));
            }
        }

        /// <summary>
        /// Invokes all registered shutdown actions in reverse-registration order (LIFO).
        /// Exceptions thrown by individual hooks are caught and logged; they do not prevent
        /// subsequent hooks from running. After the first call, subsequent calls are no-ops.
        /// </summary>
        public static void Shutdown()
        {
            // Fast path: already shut down.
            if (_shuttingDown) return;

            List<(string Name, Action Action)> hooks;
            lock (_lock)
            {
                if (_shuttingDown) return;
                _shuttingDown = true;

                // Snapshot in LIFO order.
                hooks = new List<(string Name, Action Action)>(_hooks);
                hooks.Reverse();
                _hooks.Clear();
            }

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
