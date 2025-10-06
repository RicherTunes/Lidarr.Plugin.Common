using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// Interface for managing CLI application state and session data
    /// Handles temporary data that persists during CLI sessions
    /// </summary>
    public interface IStateService
    {
        /// <summary>
        /// Initialize the state service
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Set a state value
        /// </summary>
        Task SetAsync<T>(string key, T value);

        /// <summary>
        /// Get a state value
        /// </summary>
        Task<T> GetAsync<T>(string key);

        /// <summary>
        /// Try to get a state value; returns null when not found or mismatched type.
        /// </summary>
        Task<T?> TryGetAsync<T>(string key);

        /// <summary>
        /// Check if state exists for key
        /// </summary>
        Task<bool> ExistsAsync(string key);

        /// <summary>
        /// Remove state for key
        /// </summary>
        Task RemoveAsync(string key);

        /// <summary>
        /// Clear all state data
        /// </summary>
        Task ClearAsync();

        /// <summary>
        /// Get all state keys
        /// </summary>
        Task<IEnumerable<string>> GetKeysAsync();

        /// <summary>
        /// Save state to persistent storage
        /// </summary>
        Task SaveAsync();

        /// <summary>
        /// Load state from persistent storage
        /// </summary>
        Task LoadAsync();
    }
}
