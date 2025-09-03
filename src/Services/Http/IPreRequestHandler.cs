using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Optional pre-request hook to unify session checks and auth/signature injection.
    /// Additive and safe: consumers can ignore if not needed.
    /// </summary>
    public interface IPreRequestHandler
    {
        /// <summary>
        /// Ensure a valid session exists prior to issuing an API call.
        /// </summary>
        Task EnsureValidSessionAsync();

        /// <summary>
        /// Inject required auth parameters (e.g., app_id, token) into the query map.
        /// </summary>
        void InjectAuthParameters(IDictionary<string, string> parameters);

        /// <summary>
        /// Sign the request if the endpoint requires it.
        /// </summary>
        void SignIfRequired(string endpoint, IDictionary<string, string> parameters);
    }
}

