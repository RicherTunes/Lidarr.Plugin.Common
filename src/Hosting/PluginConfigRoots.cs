// <copyright file="PluginConfigRoots.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;

namespace Lidarr.Plugin.Common.Hosting
{
    /// <summary>
    /// Resolves the per-plugin configuration root directory. The chain matches
    /// the convention used across Lidarr.* plugins:
    /// 1. <c>LIDARR_PLUGIN_CONFIG</c> environment variable (override)
    /// 2. <c>/config</c> when present (Docker hotio/linuxserver convention)
    /// 3. Windows: <c>%AppData%</c>
    /// 4. Linux/macOS: <c>$XDG_CONFIG_HOME</c>, then <c>$HOME/.config</c>
    /// 5. Last resort: <c>/config/&lt;appName&gt;</c>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This consolidates the hand-rolled chain that several plugins (tidalarr,
    /// applemusicarr, qobuzarr) replicated independently, fixing two subtle bugs
    /// commonly seen in those copies:
    /// </para>
    /// <list type="bullet">
    /// <item><description>HOME may point at <c>/root</c> for non-root container users; <c>/config</c> is preferred when present.</description></item>
    /// <item><description>XDG_CONFIG_HOME is honored (POSIX convention) before falling back to <c>$HOME/.config</c>.</description></item>
    /// </list>
    /// </remarks>
    public static class PluginConfigRoots
    {
        /// <summary>
        /// Environment variable for an explicit override. Takes precedence over every
        /// other branch when set to a non-empty value. Useful for tests and CI.
        /// </summary>
        public const string OverrideEnvVar = "LIDARR_PLUGIN_CONFIG";

        /// <summary>
        /// Default Docker config root (hotio/linuxserver convention).
        /// </summary>
        public const string DefaultDockerConfigRoot = "/config";

        /// <summary>
        /// Resolves the configuration root for the specified application.
        /// </summary>
        /// <param name="appName">Plugin/application name used as the leaf directory.</param>
        /// <returns>An absolute path. Existence of the directory is not guaranteed; callers should create it as needed.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="appName"/> is null or whitespace.</exception>
        public static string Resolve(string appName)
        {
            return Resolve(appName, environment: null);
        }

        /// <summary>
        /// Resolves the configuration root for the specified application using a custom
        /// environment provider. Primarily intended for tests; production code should
        /// use the single-argument overload.
        /// </summary>
        /// <param name="appName">Plugin/application name used as the leaf directory.</param>
        /// <param name="environment">Optional environment provider. When null, the process environment is used.</param>
        /// <returns>An absolute path.</returns>
        public static string Resolve(string appName, IConfigEnvironment? environment)
        {
            if (string.IsNullOrWhiteSpace(appName))
            {
                throw new ArgumentException("Application name must be supplied", nameof(appName));
            }

            // Defense-in-depth: appName becomes a directory under a parent root, so
            // path-traversal segments would escape the intended root. Reject them
            // explicitly even though Path.Combine doesn't traverse `..` on its own —
            // the resolved string would still contain `..` which downstream code may
            // not normalize before file IO.
            if (appName.Contains("..", StringComparison.Ordinal) ||
                appName.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                throw new ArgumentException(
                    "Application name must not contain path separators or traversal segments (got '" + appName + "').",
                    nameof(appName));
            }

            environment ??= ProcessConfigEnvironment.Instance;

            // 1. Explicit override
            var overridden = environment.GetEnvironmentVariable(OverrideEnvVar);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return Path.Combine(overridden, appName);
            }

            // 2. Docker /config when present (avoid HOME=/root traps)
            var dockerRoot = DefaultDockerConfigRoot;
            if (environment.DirectoryExists(dockerRoot))
            {
                return Path.Combine(dockerRoot, appName);
            }

            // 3. Windows %AppData%
            var appData = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, appName);
            }

            // 4. Linux/macOS XDG_CONFIG_HOME, then ~/.config
            var xdg = environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
            {
                return Path.Combine(xdg, appName);
            }

            var home = environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".config", appName);
            }

            // 5. Last resort
            return Path.Combine(DefaultDockerConfigRoot, appName);
        }
    }

    /// <summary>
    /// Test seam for <see cref="PluginConfigRoots.Resolve(string, IConfigEnvironment?)"/>.
    /// Production code should not implement this — use the single-argument overload of
    /// <see cref="PluginConfigRoots.Resolve(string)"/> instead.
    /// </summary>
    public interface IConfigEnvironment
    {
        /// <summary>
        /// Gets the value of an environment variable.
        /// </summary>
        /// <param name="name">Variable name.</param>
        /// <returns>Variable value or null when unset.</returns>
        string? GetEnvironmentVariable(string name);

        /// <summary>
        /// Gets a special folder path (e.g., <see cref="Environment.SpecialFolder.ApplicationData"/>).
        /// </summary>
        /// <param name="folder">Folder kind.</param>
        /// <returns>Folder path; may be empty when not available.</returns>
        string GetFolderPath(Environment.SpecialFolder folder);

        /// <summary>
        /// Tests whether a directory exists.
        /// </summary>
        /// <param name="path">Directory path.</param>
        /// <returns>True if the directory exists.</returns>
        bool DirectoryExists(string path);
    }

    /// <summary>
    /// Default <see cref="IConfigEnvironment"/> implementation backed by the running
    /// process. Singleton via <see cref="Instance"/>.
    /// </summary>
    public sealed class ProcessConfigEnvironment : IConfigEnvironment
    {
        /// <summary>
        /// Gets the shared instance.
        /// </summary>
        public static ProcessConfigEnvironment Instance { get; } = new();

        private ProcessConfigEnvironment()
        {
        }

        /// <inheritdoc />
        public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

        /// <inheritdoc />
        public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        /// <inheritdoc />
        public bool DirectoryExists(string path) => Directory.Exists(path);
    }
}
