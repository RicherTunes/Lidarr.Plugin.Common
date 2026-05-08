// <copyright file="LlmRequestTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Text.Json;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.Llm;

public class LlmRequestTests
{
    [Fact]
    public void JsonMode_DefaultsToFalse()
    {
        var req = new LlmRequest { Prompt = "hello" };
        Assert.False(req.JsonMode);
    }

    [Fact]
    public void JsonMode_CanBeOptedIn()
    {
        var req = new LlmRequest
        {
            Prompt = "Return user as JSON",
            JsonMode = true,
        };

        Assert.True(req.JsonMode);
    }

    [Fact]
    public void JsonMode_RoundTrips_ThroughJsonSerialization()
    {
        var original = new LlmRequest
        {
            Prompt = "p",
            JsonMode = true,
        };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<LlmRequest>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.JsonMode);
    }

    [Fact]
    public void Existing_Properties_StillWorkAlongsideJsonMode()
    {
        var req = new LlmRequest
        {
            Prompt = "p",
            SystemPrompt = "be helpful",
            Model = "gpt-4",
            Temperature = 0.7f,
            MaxTokens = 100,
            JsonMode = true,
        };

        Assert.Equal("p", req.Prompt);
        Assert.Equal("be helpful", req.SystemPrompt);
        Assert.Equal("gpt-4", req.Model);
        Assert.Equal(0.7f, req.Temperature);
        Assert.Equal(100, req.MaxTokens);
        Assert.True(req.JsonMode);
    }
}
