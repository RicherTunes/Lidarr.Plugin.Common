using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// JSON-based configuration service for persistent CLI settings
    /// Stores configuration in user's application data directory
    /// </summary>
    public class JsonConfigService : IConfigService
    {
        private readonly string _configDirectory;
        private readonly JsonSerializerSettings _jsonSettings;

        public JsonConfigService(string appName = "StreamingPlugin")
        {
            _configDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                appName
            );

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };

            Directory.CreateDirectory(_configDirectory);
        }

        public async Task SaveAsync<T>(string key, T value)
        {
            var filePath = GetFilePath(key);
            var json = JsonConvert.SerializeObject(value, _jsonSettings);
            await File.WriteAllTextAsync(filePath, json);
        }

        public async Task<T> LoadAsync<T>(string key)
        {
            var filePath = GetFilePath(key);
            
            if (!File.Exists(filePath))
                return default(T);

            var json = await File.ReadAllTextAsync(filePath);
            return JsonConvert.DeserializeObject<T>(json, _jsonSettings);
        }

        public Task<bool> ExistsAsync(string key)
        {
            var filePath = GetFilePath(key);
            return Task.FromResult(File.Exists(filePath));
        }

        public Task DeleteAsync(string key)
        {
            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return Task.CompletedTask;
        }

        public Task ClearAllAsync()
        {
            if (Directory.Exists(_configDirectory))
            {
                Directory.Delete(_configDirectory, recursive: true);
                Directory.CreateDirectory(_configDirectory);
            }
            return Task.CompletedTask;
        }

        public string GetConfigDirectory()
        {
            return _configDirectory;
        }

        private string GetFilePath(string key)
        {
            var safeKey = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_configDirectory, $"{safeKey}.json");
        }
    }
}