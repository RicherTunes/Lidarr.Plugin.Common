// <copyright file="DecoderRoutingTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Streaming;
using Lidarr.Plugin.Common.Streaming.Decoders;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

/// <summary>
/// Tests for decoder routing to catch provider ID drift.
/// Update these tests when adding new providers.
/// </summary>
public class DecoderRoutingTests
{
    /// <summary>
    /// Source of truth for OpenAI-compatible provider IDs.
    /// Update this list when adding support for new providers.
    /// </summary>
    private static readonly HashSet<string> KnownOpenAiCompatibleProviders = new()
    {
        "openai",
        "azure-openai",
        "openrouter",
        "together",
        "anyscale",
        "fireworks",
        // Add new providers here when supported
    };

    /// <summary>
    /// Source of truth for Gemini provider IDs.
    /// </summary>
    private static readonly HashSet<string> KnownGeminiProviders = new()
    {
        "gemini",
        "google",
    };

    /// <summary>
    /// Source of truth for Z.AI GLM provider IDs.
    /// </summary>
    private static readonly HashSet<string> KnownZaiProviders = new()
    {
        "zai",
        "glm",
        "zhipu",
    };

    [Fact]
    public void OpenAiDecoder_SupportedProviders_MatchesKnownList()
    {
        // This test catches drift between the decoder's provider list
        // and this source-of-truth list
        var decoder = new OpenAiStreamDecoder();
        var decoderProviders = decoder.SupportedProviderIds.ToHashSet();

        // All known providers should be in the decoder
        foreach (var provider in KnownOpenAiCompatibleProviders)
        {
            Assert.Contains(provider, decoderProviders);
        }

        // All decoder providers should be in the known list
        foreach (var provider in decoderProviders)
        {
            Assert.Contains(provider, KnownOpenAiCompatibleProviders);
        }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure-openai")]
    [InlineData("openrouter")]
    [InlineData("together")]
    [InlineData("anyscale")]
    [InlineData("fireworks")]
    public void OpenAiDecoder_CanDecodeForProvider_KnownProviders(string providerId)
    {
        var decoder = new OpenAiStreamDecoder();
        Assert.True(decoder.CanDecodeForProvider(providerId, "text/event-stream"));
    }

    [Theory]
    [InlineData("gemini")] // Will need GeminiStreamDecoder
    [InlineData("anthropic")] // Will need AnthropicStreamDecoder
    [InlineData("zai")] // Will need ZaiStreamDecoder
    [InlineData("ollama")] // May use OpenAI format but verify
    [InlineData("unknown-provider")]
    public void OpenAiDecoder_CanDecodeForProvider_UnknownProviders_ReturnsFalse(string providerId)
    {
        var decoder = new OpenAiStreamDecoder();
        Assert.False(decoder.CanDecodeForProvider(providerId, "text/event-stream"));
    }

    [Fact]
    public void OpenAiDecoder_CanDecode_TextEventStream_ReturnsTrue()
    {
        var decoder = new OpenAiStreamDecoder();

        Assert.True(decoder.CanDecode("text/event-stream"));
        Assert.True(decoder.CanDecode("text/event-stream; charset=utf-8"));
        Assert.True(decoder.CanDecode("TEXT/EVENT-STREAM")); // Case insensitive
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("application/x-ndjson")]
    [InlineData("")]
    public void OpenAiDecoder_CanDecode_OtherContentTypes_ReturnsFalse(string contentType)
    {
        var decoder = new OpenAiStreamDecoder();
        Assert.False(decoder.CanDecode(contentType));
    }

    // Gemini decoder routing tests

    [Fact]
    public void GeminiDecoder_SupportedProviders_MatchesKnownList()
    {
        var decoder = new GeminiStreamDecoder();
        var decoderProviders = decoder.SupportedProviderIds.ToHashSet();

        foreach (var provider in KnownGeminiProviders)
        {
            Assert.Contains(provider, decoderProviders);
        }

        foreach (var provider in decoderProviders)
        {
            Assert.Contains(provider, KnownGeminiProviders);
        }
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("google")]
    public void GeminiDecoder_CanDecodeForProvider_KnownProviders(string providerId)
    {
        var decoder = new GeminiStreamDecoder();
        Assert.True(decoder.CanDecodeForProvider(providerId, "text/event-stream"));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("zai")]
    [InlineData("anthropic")]
    public void GeminiDecoder_CanDecodeForProvider_UnknownProviders_ReturnsFalse(string providerId)
    {
        var decoder = new GeminiStreamDecoder();
        Assert.False(decoder.CanDecodeForProvider(providerId, "text/event-stream"));
    }

    // Z.AI decoder routing tests

    [Fact]
    public void ZaiDecoder_SupportedProviders_MatchesKnownList()
    {
        var decoder = new ZaiStreamDecoder();
        var decoderProviders = decoder.SupportedProviderIds.ToHashSet();

        foreach (var provider in KnownZaiProviders)
        {
            Assert.Contains(provider, decoderProviders);
        }

        foreach (var provider in decoderProviders)
        {
            Assert.Contains(provider, KnownZaiProviders);
        }
    }

    [Theory]
    [InlineData("zai")]
    [InlineData("glm")]
    [InlineData("zhipu")]
    public void ZaiDecoder_CanDecodeForProvider_KnownProviders(string providerId)
    {
        var decoder = new ZaiStreamDecoder();
        Assert.True(decoder.CanDecodeForProvider(providerId, "text/event-stream"));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("gemini")]
    [InlineData("anthropic")]
    public void ZaiDecoder_CanDecodeForProvider_UnknownProviders_ReturnsFalse(string providerId)
    {
        var decoder = new ZaiStreamDecoder();
        Assert.False(decoder.CanDecodeForProvider(providerId, "text/event-stream"));
    }

    // Cross-decoder exclusivity tests

    [Fact]
    public void AllDecoders_HaveDisjointProviderIds()
    {
        var openAi = new OpenAiStreamDecoder().SupportedProviderIds.ToHashSet();
        var gemini = new GeminiStreamDecoder().SupportedProviderIds.ToHashSet();
        var zai = new ZaiStreamDecoder().SupportedProviderIds.ToHashSet();

        // No provider should be claimed by multiple decoders
        Assert.Empty(openAi.Intersect(gemini));
        Assert.Empty(openAi.Intersect(zai));
        Assert.Empty(gemini.Intersect(zai));
    }

    [Theory]
    [InlineData("openai", "openai")]
    [InlineData("gemini", "gemini")]
    [InlineData("zai", "zai")]
    public void AllDecoders_HaveUniqueDecoderId(string providerId, string expectedDecoderId)
    {
        IStreamDecoder decoder = providerId switch
        {
            "openai" => new OpenAiStreamDecoder(),
            "gemini" => new GeminiStreamDecoder(),
            "zai" => new ZaiStreamDecoder(),
            _ => throw new System.ArgumentException($"Unknown provider: {providerId}"),
        };

        Assert.Equal(expectedDecoderId, decoder.DecoderId);
    }
}
