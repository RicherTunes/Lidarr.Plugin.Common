using System.Text.Json;

namespace Lidarr.Plugin.Abstractions.Results
{
    /// <summary>
    /// Convenience JSON serializer for PluginOperationResult types to keep host/tooling assertions consistent.
    /// </summary>
    public static class PluginOperationResultJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public static string ToJson(PluginOperationResult result)
            => JsonSerializer.Serialize(new
            {
                success = result.IsSuccess,
                error = result.Error is null ? null : new
                {
                    code = result.Error.Code.ToString(),
                    message = result.Error.Message,
                    metadata = result.Error.Metadata
                }
            }, Options);

        public static string ToJson<T>(PluginOperationResult<T> result)
            => JsonSerializer.Serialize(new
            {
                success = result.IsSuccess,
                value = result.Value,
                error = result.Error is null ? null : new
                {
                    code = result.Error.Code.ToString(),
                    message = result.Error.Message,
                    metadata = result.Error.Metadata
                }
            }, Options);
    }
}
