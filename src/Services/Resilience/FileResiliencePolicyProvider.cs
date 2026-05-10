using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// JSON-file-backed <see cref="IResilienceSettingsProvider"/> with debounced
    /// hot-reload via <see cref="FileSystemWatcher"/>. Falls back to a
    /// <see cref="StaticResiliencePolicyProvider"/> when the file is missing,
    /// unreadable, or doesn't define the requested profile.
    /// </summary>
    /// <remarks>
    /// File schema:
    /// <code>
    /// {
    ///   "profiles": {
    ///     "search":   { "maxRetries": 6, "retryBudget": "00:01:00", ... },
    ///     "download": { "maxRetries": 3, ... }
    ///   }
    /// }
    /// </code>
    /// Reload is debounced for 150 ms after any FileSystemWatcher event so a
    /// single editor save (which often produces a flurry of Created/Changed/Renamed
    /// events) results in one re-read.
    /// </remarks>
    public sealed class FileResiliencePolicyProvider : IResilienceSettingsProvider, IDisposable
    {
        private readonly string _path;
        private readonly IResilienceSettingsProvider _fallback;
        private readonly ILogger _logger;
        private readonly FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, ResilienceProfileSettings> _cache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ResilienceProfileSettings> _profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private Timer? _debounce;
        private bool _disposed;

        private static readonly JsonSerializerOptions JsonCaseInsensitive = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>The debounce delay between FileSystemWatcher events and reload.</summary>
        public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// Creates a new file-backed policy provider.
        /// </summary>
        /// <param name="path">Absolute path to the JSON profile file (need not exist at construction).</param>
        /// <param name="fallback">Provider used when the file doesn't define a requested profile. Defaults to <see cref="StaticResiliencePolicyProvider"/>.</param>
        /// <param name="logger">Optional logger; null = silent.</param>
        public FileResiliencePolicyProvider(
            string path,
            IResilienceSettingsProvider? fallback = null,
            ILogger<FileResiliencePolicyProvider>? logger = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _fallback = fallback ?? new StaticResiliencePolicyProvider();
            _logger = (ILogger?)logger ?? NullLogger.Instance;

            TryLoad();

            try
            {
                var dir = Path.GetDirectoryName(_path);
                var file = Path.GetFileName(_path);
                if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(file))
                {
                    if (!Directory.Exists(dir))
                    {
                        _logger.LogDebug("Resilience config directory '{Dir}' does not exist; watcher disabled until startup.", dir);
                    }
                    else
                    {
                        _watcher = new FileSystemWatcher(dir, file)
                        {
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                            EnableRaisingEvents = true
                        };
                        _watcher.Changed += (_, __) => DebouncedReload();
                        _watcher.Created += (_, __) => DebouncedReload();
                        _watcher.Renamed += (_, __) => DebouncedReload();
                        _watcher.Deleted += (_, __) => DebouncedReload();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create FileSystemWatcher for resilience config '{Path}'. Hot-reload disabled.", _path);
            }
        }

        /// <inheritdoc/>
        public ResilienceProfileSettings Get(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) profileName = "default";
            if (_cache.TryGetValue(profileName, out var cached)) return cached;
            if (_profiles.TryGetValue(profileName, out var fromFile))
            {
                _cache[profileName] = fromFile;
                return fromFile;
            }
            var fb = _fallback.Get(profileName);
            _cache[profileName] = fb;
            return fb;
        }

        /// <summary>Forces a synchronous reload of the configuration file. Mostly useful for tests.</summary>
        public void ForceReload() => TryLoad();

        private void DebouncedReload()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _debounce?.Dispose();
                _debounce = new Timer(_ => TryLoad(), null, DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }

        private void TryLoad()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    // Reset to empty so fallback is consulted; clear cache so the next Get returns fresh fallback.
                    _profiles = new Dictionary<string, ResilienceProfileSettings>(StringComparer.OrdinalIgnoreCase);
                    _cache.Clear();
                    return;
                }

                var json = File.ReadAllText(_path);
                var root = JsonSerializer.Deserialize<Root>(json, JsonCaseInsensitive);
                if (root?.Profiles is { Count: > 0 })
                {
                    _profiles = new Dictionary<string, ResilienceProfileSettings>(root.Profiles, StringComparer.OrdinalIgnoreCase);
                    _cache.Clear();
                    _logger.LogDebug("Loaded {Count} resilience profiles from {Path}", _profiles.Count, _path);
                }
            }
            catch (Exception ex)
            {
                // Tolerate invalid JSON / IO errors; keep last-known-good state and let fallback take over.
                _logger.LogWarning(ex, "Failed to load resilience config '{Path}'. Falling back to existing profiles.", _path);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _debounce?.Dispose(); } catch { /* ignore */ }
            try { _watcher?.Dispose(); } catch { /* ignore */ }
        }

        private sealed class Root
        {
            public Dictionary<string, ResilienceProfileSettings>? Profiles { get; set; }
        }
    }
}
