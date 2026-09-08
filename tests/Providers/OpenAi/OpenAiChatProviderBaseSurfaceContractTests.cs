using System;
using System.Linq;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.OpenAi;

/// <summary>
/// Pins the Common-owned public seam before its implementation exists. Reflection keeps this
/// an executable behavioral assertion rather than a missing-type compile error during RED.
/// </summary>
public sealed class OpenAiChatProviderBaseSurfaceContractTests
{
    private const string Namespace = "Lidarr.Plugin.Common.Providers.OpenAi";

    [Fact]
    public void PublicContract_ExposesTheSharedProviderBaseAndItsInjectionSeams()
    {
        var assembly = typeof(LlmErrorMapper).Assembly;
        var providerBase = assembly.GetType($"{Namespace}.OpenAiChatProviderBase");
        var transport = assembly.GetType($"{Namespace}.IOpenAiChatTransport");
        var authCircuit = assembly.GetType($"{Namespace}.IOpenAiChatAuthCircuit");

        Assert.NotNull(providerBase);
        Assert.True(providerBase!.IsAbstract);
        Assert.True(typeof(ILlmProvider).IsAssignableFrom(providerBase));
        Assert.NotNull(transport);
        Assert.True(transport!.IsInterface);
        Assert.NotNull(authCircuit);
        Assert.True(authCircuit!.IsInterface);

        var hookNames = new[]
        {
            "BuildHealthProbeBody",
            "AddCompletionRequestHeaders",
            "AddStreamingRequestHeaders",
            "MapHttpError",
            "ParseCompletion",
            "TransformStreamChunk",
            "NormalizeModel",
            "UpdateModel",
        };

        var methods = providerBase.GetMethods(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        foreach (var hookName in hookNames)
        {
            Assert.Contains(methods, method => method.Name == hookName);
        }

        Assert.Contains(providerBase.GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == transport));
    }
}
