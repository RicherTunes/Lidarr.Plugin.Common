using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// File-based state service for CLI session management
    /// Provides thread-safe in-memory state with optional persistence
    /// </summary>
    public class FileStateService : IStateService
    {
        private readonly ConcurrentDictionary<string, object> _state;
        private readonly string _stateFilePath;
        private readonly JsonSerializerSettings _jsonSettings;

        /// <summary>
        /// Creates a new FileStateService with the specified app name.
        /// State files will be stored in LocalApplicationData/[appName]/session-state.json
        /// </summary>
        public FileStateService(string appName = "StreamingPlugin")
        {
            _state = new ConcurrentDictionary<string, object>();

            var stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                appName
            );

            Directory.CreateDirectory(stateDirectory);
            _stateFilePath = Path.Combine(stateDirectory, "session-state.json");

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                TypeNameHandling = TypeNameHandling.Auto
            };
        }

        /// <summary>
        /// Constructor for testing purposes. Allows specifying a custom state file path.
        /// </summary>
        /// <param name="stateFilePath">Full path to the state file (not directory).</param>
        internal FileStateService(string stateFilePath, bool forTesting)
        {
            _state = new ConcurrentDictionary<string, object>();

            var stateDirectory = Path.GetDirectoryName(stateFilePath)
                ?? throw new ArgumentException("Invalid state file path", nameof(stateFilePath));

            Directory.CreateDirectory(stateDirectory);
            _stateFilePath = stateFilePath;

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                TypeNameHandling = TypeNameHandling.Auto
            };
        }

        public async Task InitializeAsync()
        {
            await LoadAsync();
        }

        public Task SetAsync<T>(string key, T value)
        {
            _state.AddOrUpdate(key, value, (k, v) => value);
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            if (_state.TryGetValue(key, out var value) && value is T typedValue)
            {
                return Task.FromResult(typedValue);
            }

            // If not found or wrong type, surface a clear error instead of returning null/default for reference types.
            throw new KeyNotFoundException($"State value '{key}' not found or not of type {typeof(T).Name}.");
        }

        public Task<T?> TryGetAsync<T>(string key)
        {
            if (_state.TryGetValue(key, out var value) && value is T typedValue)
            {
                return Task.FromResult<T?>(typedValue);
            }
            return Task.FromResult<T?>(default);
        }

        public Task<bool> ExistsAsync(string key)
        {
            return Task.FromResult(_state.ContainsKey(key));
        }

        public Task RemoveAsync(string key)
        {
            _state.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            _state.Clear();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<string>> GetKeysAsync()
        {
            return Task.FromResult<IEnumerable<string>>(_state.Keys);
        }

        public async Task SaveAsync()
        {
            try
            {
                var stateData = new Dictionary<string, object>(_state);
                var json = JsonConvert.SerializeObject(stateData, _jsonSettings);
                await File.WriteAllTextAsync(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                // Log error but don't fail - state service should be resilient
                // In a real implementation, you'd use ILogger here
                Console.WriteLine($"Warning: Failed to save state: {ex.Message}");
            }
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                    return;

                var json = await File.ReadAllTextAsync(_stateFilePath);
                var stateData = JsonConvert.DeserializeObject<Dictionary<string, object>>(json, _jsonSettings);
                
                if (stateData != null)
                {
                    _state.Clear();
                    foreach (var kvp in stateData)
                    {
                        _state.TryAdd(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail - state service should be resilient
                Console.WriteLine($"Warning: Failed to load state: {ex.Message}");
            }
        }
    }
}
