// <copyright file="LlmToolTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Text.Json;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Abstractions.Llm;

/// <summary>
/// Unit tests for the Phase 5e P1 tool-calling primitives:
/// <see cref="LlmTool"/>, <see cref="LlmToolCall"/>, <see cref="LlmToolResult"/>,
/// <see cref="LlmToolKind"/>, <see cref="LlmToolChoice"/>, plus the
/// <see cref="LlmRequest.Tools"/>/<see cref="LlmRequest.ToolChoice"/>/<see cref="LlmRequest.ToolResults"/>
/// and <see cref="LlmResponse.ToolCalls"/> additions.
/// </summary>
public class LlmToolTests
{
    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void LlmTool_RecordEquality_IsValueBased()
    {
        var schema = ParseSchema("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}");
        var a = new LlmTool("search", "Search the web", schema);
        var b = new LlmTool("search", "Search the web", schema);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void LlmTool_DefaultsKindToFunction()
    {
        var schema = ParseSchema("{}");
        var tool = new LlmTool("noop", string.Empty, schema);

        Assert.Equal(LlmToolKind.Function, tool.Kind);
    }

    [Fact]
    public void LlmTool_ParametersSchema_RoundTripsThroughJsonSerialization()
    {
        // Schema fidelity — the JsonElement payload must survive a full
        // JsonSerializer round-trip without lossy structural change.
        var original = new LlmTool(
            Name: "lookup_track",
            Description: "Look up a track by id",
            ParametersSchema: ParseSchema(
                "{\"type\":\"object\",\"required\":[\"id\"],\"properties\":{\"id\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1}}}"));

        var json = JsonSerializer.Serialize(original);
        var rehydrated = JsonSerializer.Deserialize<LlmTool>(json);

        Assert.NotNull(rehydrated);
        Assert.Equal("lookup_track", rehydrated!.Name);
        Assert.Equal("Look up a track by id", rehydrated.Description);
        Assert.Equal(LlmToolKind.Function, rehydrated.Kind);

        // Verify schema structure preserved
        Assert.Equal(JsonValueKind.Object, rehydrated.ParametersSchema.ValueKind);
        Assert.True(rehydrated.ParametersSchema.TryGetProperty("required", out var required));
        Assert.Equal(JsonValueKind.Array, required.ValueKind);
        Assert.True(rehydrated.ParametersSchema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("id", out var idProp));
        Assert.Equal("string", idProp.GetProperty("type").GetString());
    }

    [Fact]
    public void LlmRequest_BuildsWithTools_AndIsBackwardCompatible()
    {
        // Backward-compat: existing required+optional members still compile and
        // default exactly as before. Adding Tools/ToolChoice/ToolResults must not
        // disturb the existing builder shape.
        var existingShapeRequest = new LlmRequest
        {
            Prompt = "p",
            SystemPrompt = "s",
            Model = "gpt-4",
            Temperature = 0.5f,
            MaxTokens = 100,
            JsonMode = true,
        };

        Assert.Null(existingShapeRequest.Tools);
        Assert.Equal(LlmToolChoice.Auto, existingShapeRequest.ToolChoice);
        Assert.Null(existingShapeRequest.ToolResults);

        var withTools = new LlmRequest
        {
            Prompt = "find a track",
            Tools = new List<LlmTool>
            {
                new("lookup", "lookup", ParseSchema("{\"type\":\"object\"}")),
            },
            ToolChoice = LlmToolChoice.Required,
        };

        Assert.NotNull(withTools.Tools);
        Assert.Single(withTools.Tools!);
        Assert.Equal("lookup", withTools.Tools![0].Name);
        Assert.Equal(LlmToolChoice.Required, withTools.ToolChoice);
    }

    [Fact]
    public void LlmRequest_ToolChoice_DefaultsToAuto()
    {
        var req = new LlmRequest { Prompt = "hello" };
        Assert.Equal(LlmToolChoice.Auto, req.ToolChoice);
    }

    [Fact]
    public void LlmRequest_ToolResults_CarriesPriorOutputs()
    {
        var req = new LlmRequest
        {
            Prompt = "follow-up",
            ToolResults = new List<LlmToolResult>
            {
                new("call_abc", "{\"ok\":true}"),
                new("call_xyz", "boom", IsError: true),
            },
        };

        Assert.NotNull(req.ToolResults);
        Assert.Equal(2, req.ToolResults!.Count);
        Assert.Equal("call_abc", req.ToolResults[0].ToolCallId);
        Assert.False(req.ToolResults[0].IsError);
        Assert.True(req.ToolResults[1].IsError);
    }

    [Fact]
    public void LlmResponse_BuildsWithToolCalls_AndIsBackwardCompatible()
    {
        // Backward-compat: existing minimal LlmResponse still constructs.
        var legacyResponse = new LlmResponse { Content = "hi" };
        Assert.Null(legacyResponse.ToolCalls);

        // New shape carries tool calls when the model emitted them.
        var args = ParseSchema("{\"q\":\"hello\"}");
        var response = new LlmResponse
        {
            Content = string.Empty,
            FinishReason = "tool_calls",
            ToolCalls = new List<LlmToolCall>
            {
                new("call_123", "search", args, RawArguments: "{\"q\":\"hello\"}"),
            },
        };

        Assert.NotNull(response.ToolCalls);
        Assert.Single(response.ToolCalls!);
        Assert.Equal("call_123", response.ToolCalls![0].Id);
        Assert.Equal("search", response.ToolCalls[0].Name);
        Assert.Equal("{\"q\":\"hello\"}", response.ToolCalls[0].RawArguments);
    }

    [Fact]
    public void LlmToolCall_RawArguments_DefaultsToNull()
    {
        var call = new LlmToolCall("id", "name", ParseSchema("{}"));
        Assert.Null(call.RawArguments);
    }

    [Fact]
    public void LlmToolResult_Constructs_WithIsErrorFlag()
    {
        var ok = new LlmToolResult("call_1", "{\"x\":1}");
        Assert.Equal("call_1", ok.ToolCallId);
        Assert.Equal("{\"x\":1}", ok.Output);
        Assert.False(ok.IsError);

        var err = new LlmToolResult("call_2", "permission denied", IsError: true);
        Assert.True(err.IsError);
        Assert.Equal("permission denied", err.Output);

        // Record equality is value-based.
        var ok2 = new LlmToolResult("call_1", "{\"x\":1}");
        Assert.Equal(ok, ok2);
    }

    [Fact]
    public void LlmToolChoice_HasExpectedMembers()
    {
        // Sanity: enum surface is the spec'd Auto/None/Required (no surprise members),
        // so adapters can exhaustively switch.
        var members = System.Enum.GetValues<LlmToolChoice>();
        Assert.Contains(LlmToolChoice.Auto, members);
        Assert.Contains(LlmToolChoice.None, members);
        Assert.Contains(LlmToolChoice.Required, members);
        Assert.Equal(3, members.Length);
    }

    [Fact]
    public void LlmToolKind_DefaultsToFunction_ForFutureExtensibility()
    {
        // Today only Function is universally supported; the enum exists so future
        // tool kinds (web search, code interpreter, file search, ...) can be added
        // without breaking the LlmTool record contract.
        Assert.Equal(0, (int)LlmToolKind.Function);
        Assert.Equal(LlmToolKind.Function, default(LlmToolKind));
    }
}
