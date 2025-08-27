using System.Threading.Tasks;
using System.Threading;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// Interface for live dashboard display in CLI applications
    /// Provides real-time status updates and interactive monitoring
    /// </summary>
    public interface IDashboard
    {
        /// <summary>
        /// Start the live dashboard
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stop the dashboard
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Update dashboard display
        /// </summary>
        Task RefreshAsync();

        /// <summary>
        /// Check if dashboard is running
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Set refresh interval in milliseconds
        /// </summary>
        int RefreshIntervalMs { get; set; }
    }
}