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

            // Try to convert if types don't match exactly
            if (value != null)
            {
                try
                {
                    var convertedValue = (T)Convert.ChangeType(value, typeof(T));
                    return Task.FromResult(convertedValue);
                }
                catch
                {
                    // Conversion failed, return default
                }
            }

            return Task.FromResult(default(T));
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