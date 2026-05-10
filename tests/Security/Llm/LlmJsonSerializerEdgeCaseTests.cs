// <copyright file="LlmJsonSerializerTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Security.Llm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Security.Llm;

/// <summary>
/// Additional edge-case coverage for <see cref="LlmJsonSerializer"/> beyond the inline
/// tests embedded in <c>LlmPromptSanitizerTests.cs</c>. Targets the async stream variants,
/// strict-mode behavior, ValidateJsonContent surface, and CreateOptions defaults.
/// </summary>
[Trait("Category", "Unit")]
public class LlmJsonSerializerEdgeCaseTests
{
    private sealed class Payload
    {
        public string? Name { get; set; }
        public int Count { get; set; }
    }

    [Fact]
    public void Deserialize_ValidJson_ReturnsObject()
    {
        var p = LlmJsonSerializer.Deserialize<Payload>("{\"name\":\"foo\",\"count\":3}");
        Assert.NotNull(p);
        Assert.Equal("foo", p.Name);
        Assert.Equal(3, p.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_NullOrEmpty_Throws(string? input)
    {
        Assert.Throws<ArgumentNullException>(() => LlmJsonSerializer.Deserialize<Payload>(input!));
    }

    [Fact]
    public void Deserialize_ExceedsMaxSize_Throws()
    {
        // 10 MiB + 1 of arbitrary characters — must not include suspicious patterns.
        var oversized = new string('x', LlmJsonSerializer.MaxJsonSize + 1);
        var ex = Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.Deserialize<Payload>(oversized));
        Assert.Contains("maximum allowed size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"__proto__\":1}")]
    [InlineData("{\"data\":\"javascript:alert(1)\"}")]
    [InlineData("{\"x\":\"<script>\"}")]
    [InlineData("{\"$type\":\"System.IO.FileStream\"}")]
    public void Deserialize_SuspiciousPattern_Rejected(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.Deserialize<Payload>(json));
        Assert.Contains("malicious", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_MalformedJson_WrappedAsInvalidOperation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.Deserialize<Payload>("{not valid"));
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void Deserialize_NullDeserialized_Throws()
    {
        // Top-level "null" deserializes to null reference for class types.
        var ex = Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.Deserialize<Payload>("null"));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_StrictMode_DoesNotApplyCamelCasePolicy()
    {
        // Default mode applies CamelCase naming policy, so "name" binds. Strict mode does not
        // apply that policy AND is case-sensitive — lowercase JSON keys won't bind to PascalCase props.
        var defaultMode = LlmJsonSerializer.Deserialize<Payload>("{\"name\":\"def\",\"count\":1}");
        Assert.Equal("def", defaultMode.Name);
        Assert.Equal(1, defaultMode.Count);

        var strictMode = LlmJsonSerializer.Deserialize<Payload>("{\"name\":\"strict\",\"count\":1}", strict: true);
        // Strict: case-sensitive, no naming policy — "name" won't bind to property "Name".
        Assert.Null(strictMode.Name);
        Assert.Equal(0, strictMode.Count);
    }

    [Fact]
    public async Task DeserializeAsync_ValidStream_ReturnsObject()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"async\",\"count\":7}"));
        var p = await LlmJsonSerializer.DeserializeAsync<Payload>(ms);
        Assert.Equal("async", p.Name);
        Assert.Equal(7, p.Count);
    }

    [Fact]
    public async Task DeserializeAsync_NullStream_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LlmJsonSerializer.DeserializeAsync<Payload>(null!));
    }

    [Fact]
    public async Task DeserializeAsync_OversizedSeekableStream_Throws()
    {
        var bytes = new byte[LlmJsonSerializer.MaxJsonSize + 1];
        using var ms = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlmJsonSerializer.DeserializeAsync<Payload>(ms));
    }

    [Fact]
    public async Task DeserializeAsync_MalformedJson_WrappedAsInvalidOperation()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{not valid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlmJsonSerializer.DeserializeAsync<Payload>(ms));
    }

    [Fact]
    public void Serialize_ValidObject_ReturnsJson()
    {
        var json = LlmJsonSerializer.Serialize(new Payload { Name = "x", Count = 2 });
        Assert.Contains("\"name\":\"x\"", json);
        Assert.Contains("\"count\":2", json);
    }

    [Fact]
    public void Serialize_NullObject_ReturnsLiteralNullString()
    {
        Assert.Equal("null", LlmJsonSerializer.Serialize<Payload>(null));
    }

    [Fact]
    public async Task SerializeAsync_NullStream_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LlmJsonSerializer.SerializeAsync<Payload>(null!, new Payload()));
    }

    [Fact]
    public async Task SerializeAsync_NullObject_WritesLiteralNull()
    {
        using var ms = new MemoryStream();
        await LlmJsonSerializer.SerializeAsync<Payload>(ms, null);
        Assert.Equal("null", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task SerializeAsync_ValidObject_WritesJsonToStream()
    {
        using var ms = new MemoryStream();
        await LlmJsonSerializer.SerializeAsync(ms, new Payload { Name = "stream", Count = 9 });
        var roundTrip = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("\"name\":\"stream\"", roundTrip);
    }

    [Fact]
    public void TryDeserialize_Valid_ReturnsTrue()
    {
        var ok = LlmJsonSerializer.TryDeserialize<Payload>("{\"name\":\"ok\"}", out var result, out var error);
        Assert.True(ok);
        Assert.NotNull(result);
        Assert.Null(error);
        Assert.Equal("ok", result!.Name);
    }

    [Fact]
    public void TryDeserialize_Invalid_ReturnsFalseWithError()
    {
        var ok = LlmJsonSerializer.TryDeserialize<Payload>("{not valid", out var result, out var error);
        Assert.False(ok);
        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryDeserialize_SuspiciousPattern_ReturnsFalseWithError()
    {
        var ok = LlmJsonSerializer.TryDeserialize<Payload>("{\"x\":\"javascript:alert(1)\"}", out var result, out var error);
        Assert.False(ok);
        Assert.Null(result);
        Assert.Contains("malicious", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDocument_ValidJson_ReturnsDocument()
    {
        using var doc = LlmJsonSerializer.ParseDocument("{\"a\":1}");
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseDocument_NullOrEmpty_Throws(string? json)
    {
        Assert.Throws<ArgumentNullException>(() => LlmJsonSerializer.ParseDocument(json!));
    }

    [Fact]
    public void ParseDocument_OversizedJson_Throws()
    {
        var big = new string('y', LlmJsonSerializer.MaxJsonSize + 1);
        Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.ParseDocument(big));
    }

    [Fact]
    public void ParseDocument_SuspiciousPattern_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.ParseDocument("{\"a\":\"<script>x\"}"));
    }

    [Fact]
    public void ParseDocumentRelaxed_AllowsScriptStrings()
    {
        // Relaxed mode permits suspicious-pattern strings as legitimate content.
        using var doc = LlmJsonSerializer.ParseDocumentRelaxed("{\"snippet\":\"<script>safe</script>\"}");
        Assert.Equal("<script>safe</script>", doc.RootElement.GetProperty("snippet").GetString());
    }

    [Fact]
    public void ParseDocumentRelaxed_StillEnforcesSize()
    {
        var big = new string('z', LlmJsonSerializer.MaxJsonSize + 1);
        Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.ParseDocumentRelaxed(big));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDocumentRelaxed_NullOrEmpty_Throws(string? json)
    {
        Assert.Throws<ArgumentNullException>(() => LlmJsonSerializer.ParseDocumentRelaxed(json!));
    }

    [Fact]
    public void ParseDocumentRelaxed_StillEnforcesNestingDepth()
    {
        // Build deeply nested arrays beyond HardMaxNestingDepth.
        var depth = LlmJsonSerializer.HardMaxNestingDepth + 5;
        var json = new string('[', depth) + new string(']', depth);
        Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.ParseDocumentRelaxed(json));
    }

    [Fact]
    public void ValidateJsonContent_DeepNesting_Throws()
    {
        var depth = LlmJsonSerializer.HardMaxNestingDepth + 1;
        var json = new string('{', depth) + new string('}', depth);
        Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.ValidateJsonContent(json));
    }

    [Fact]
    public void ValidateJsonContent_ExcessiveArraySize_Throws()
    {
        // Heuristic: looks for [whitespace digits{7+} whitespace] and treats it as suspicious.
        var json = "[ 12345678 ]";
        Assert.Throws<InvalidOperationException>(() => LlmJsonSerializer.ValidateJsonContent(json));
    }

    [Fact]
    public void ValidateJsonContent_NormalJson_Passes()
    {
        // Should not throw on perfectly clean content.
        LlmJsonSerializer.ValidateJsonContent("{\"items\":[1,2,3],\"name\":\"clean\"}");
    }

    [Fact]
    public void CreateOptions_ClampedToHardMaxDepth()
    {
        var opts = LlmJsonSerializer.CreateOptions(maxDepth: 9999);
        Assert.Equal(LlmJsonSerializer.HardMaxNestingDepth, opts.MaxDepth);
    }

    [Fact]
    public void CreateOptions_ApplyDefaults()
    {
        var opts = LlmJsonSerializer.CreateOptions();
        Assert.True(opts.PropertyNameCaseInsensitive);
        Assert.False(opts.WriteIndented);
        Assert.False(opts.AllowTrailingCommas);
        Assert.Equal(JsonCommentHandling.Skip, opts.ReadCommentHandling);
        Assert.Contains(opts.Converters, c => c.GetType().Name.Contains("EnumConverter"));
    }

    [Fact]
    public void CreateOptions_CaseSensitive_AndIndented_Honored()
    {
        var opts = LlmJsonSerializer.CreateOptions(caseInsensitive: false, writeIndented: true);
        Assert.False(opts.PropertyNameCaseInsensitive);
        Assert.True(opts.WriteIndented);
    }

    [Fact]
    public void DefaultSuspiciousPatterns_NonEmpty_AndCaseInsensitive()
    {
        Assert.NotEmpty(LlmJsonSerializer.DefaultSuspiciousPatterns);
        // Implementation lowercases the haystack — capitalized variants must still be caught.
        Assert.Throws<InvalidOperationException>(() =>
            LlmJsonSerializer.ValidateJsonContent("{\"x\":\"JAVASCRIPT:alert(1)\"}"));
    }
}
