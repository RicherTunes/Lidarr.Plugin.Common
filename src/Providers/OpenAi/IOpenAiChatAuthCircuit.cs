using Lidarr.Plugin.Common.Errors;

namespace Lidarr.Plugin.Common.Providers.OpenAi;

/// <summary>Optional provider-owned auth circuit used by the shared completion pipeline.</summary>
public interface IOpenAiChatAuthCircuit
{
    bool IsOpen(string providerId, string credential, out string? reason);

    void RecordAuthFailure(string providerId, string credential, LlmProviderException error);

    void RecordSuccess(string providerId, string credential);
}
