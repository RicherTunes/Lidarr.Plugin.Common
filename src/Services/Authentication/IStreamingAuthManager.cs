using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Lightweight contract for ensuring a valid streaming session prior to API calls.
    /// Additive and optional for consumers.
    /// </summary>
    public interface IStreamingAuthManager
    {
        /// <summary>
        /// Ensures a valid session exists, performing refresh or re-auth if necessary.
        /// Implementations should be idempotent and fast when already valid.
        /// </summary>
        Task EnsureValidSessionAsync();
    }
}

