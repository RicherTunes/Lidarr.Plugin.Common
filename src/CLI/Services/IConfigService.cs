using System;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// Interface for configuration management in CLI applications
    /// Handles persistent storage of user settings and authentication data
    /// </summary>
    public interface IConfigService
    {
        /// <summary>
        /// Save configuration value for a given key
        /// </summary>
        Task SaveAsync<T>(string key, T value);

        /// <summary>
        /// Load configuration value for a given key
        /// </summary>
        Task<T> LoadAsync<T>(string key);

        /// <summary>
        /// Check if configuration exists for a given key
        /// </summary>
        Task<bool> ExistsAsync(string key);

        /// <summary>
        /// Delete configuration for a given key
        /// </summary>
        Task DeleteAsync(string key);

        /// <summary>
        /// Clear all configuration data
        /// </summary>
        Task ClearAllAsync();

        /// <summary>
        /// Get configuration directory path
        /// </summary>
        string GetConfigDirectory();
    }
}