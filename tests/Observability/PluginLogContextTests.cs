using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Observability;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// Tests for <see cref="PluginLogContext"/> — scope stacking, AsyncLocal isolation,
/// default correlationId, and concurrent path independence.
/// </summary>
public class PluginLogContextTests
{
    // ------------------------------------------------------------------ //
    // 1. Push sets Current; Dispose restores previous Current
    // ------------------------------------------------------------------ //

    [Fact]
    public void Push_SetsCurrent_Dispose_RestoresPrevious()
    {
        // Ensure no leftover scope bleeds in from a prior test.
        // (AsyncLocal is per-async-context; xunit isolates per-thread, so this is safe.)
        var ctx = PluginLogContext.Push("TestPlugin", "Op1");

        Assert.Same(ctx, PluginLogContext.Current);
        Assert.Equal("TestPlugin", ctx.PluginName);
        Assert.Equal("Op1", ctx.Operation);

        ctx.Dispose();
        Assert.Null(PluginLogContext.Current);
    }

    // ------------------------------------------------------------------ //
    // 2. Nested Push/Pop stacks correctly
    // ------------------------------------------------------------------ //

    [Fact]
    public void NestedScopes_StackAndUnstackCorrectly()
    {
        var outer = PluginLogContext.Push("Plugin", "Outer");
        Assert.Same(outer, PluginLogContext.Current);

        var inner = PluginLogContext.Push("Plugin", "Inner");
        Assert.Same(inner, PluginLogContext.Current);

        inner.Dispose();
        Assert.Same(outer, PluginLogContext.Current);

        outer.Dispose();
        Assert.Null(PluginLogContext.Current);
    }

    // ------------------------------------------------------------------ //
    // 3. Default correlationId is a non-empty GUID-shaped string
    // ------------------------------------------------------------------ //

    [Fact]
    public void DefaultCorrelationId_IsNonEmptyGuid()
    {
        using var ctx = PluginLogContext.Push("Plugin", "Op");

        Assert.NotNull(ctx.CorrelationId);
        Assert.NotEmpty(ctx.CorrelationId);
        // Guid.NewGuid().ToString("N") produces 32 hex chars
        Assert.Equal(32, ctx.CorrelationId.Length);
        // Should parse as a valid GUID
        Assert.True(Guid.TryParseExact(ctx.CorrelationId, "N", out _));
    }

    [Fact]
    public void ExplicitCorrelationId_IsPreserved()
    {
        using var ctx = PluginLogContext.Push("Plugin", "Op", correlationId: "req-abc-123");

        Assert.Equal("req-abc-123", ctx.CorrelationId);
    }

    // ------------------------------------------------------------------ //
    // 4. Provider is optional
    // ------------------------------------------------------------------ //

    [Fact]
    public void Provider_IsOptional_DefaultsToNull()
    {
        using var ctx = PluginLogContext.Push("Plugin", "Op");
        Assert.Null(ctx.Provider);
    }

    [Fact]
    public void Provider_WhenSupplied_IsPreserved()
    {
        using var ctx = PluginLogContext.Push("Plugin", "Op", provider: "tidal");
        Assert.Equal("tidal", ctx.Provider);
    }

    // ------------------------------------------------------------------ //
    // 5. LinePrefix format
    // ------------------------------------------------------------------ //

    [Fact]
    public void LinePrefix_WithProvider_IncludesProviderSegment()
    {
        using var ctx = PluginLogContext.Push("Plugin", "Search", correlationId: "abc123", provider: "tidal");

        Assert.Equal("[Search:abc123:tidal] ", ctx.LinePrefix());
    }

    [Fact]
    public void LinePrefix_WithoutProvider_OmitsProviderSegment()
    {
        using var ctx = PluginLogContext.Push("Plugin", "Search", correlationId: "abc123");

        Assert.Equal("[Search:abc123] ", ctx.LinePrefix());
    }

    // ------------------------------------------------------------------ //
    // 6. AsyncLocal flows across await boundaries
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task AsyncLocal_FlowsAcrossAwait()
    {
        using var ctx = PluginLogContext.Push("Plugin", "AsyncOp", correlationId: "flow-test");

        await Task.Yield();

        // After resuming the continuation the scope must still be visible
        Assert.Same(ctx, PluginLogContext.Current);
        Assert.Equal("flow-test", PluginLogContext.Current!.CorrelationId);
    }

    // ------------------------------------------------------------------ //
    // 7. Multiple concurrent async paths don't bleed into each other
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ConcurrentPaths_DoNotBleedScopes()
    {
        const int taskCount = 20;
        var failures = 0;
        var workTasks = new Task[taskCount];

        for (int i = 0; i < taskCount; i++)
        {
            var id = i.ToString();
            workTasks[i] = Task.Run(async () =>
            {
                using var ctx = PluginLogContext.Push("Plugin", "Op", correlationId: id);

                await Task.Yield();

                if (PluginLogContext.Current?.CorrelationId != id)
                    Interlocked.Increment(ref failures);

                await Task.Delay(1);

                if (PluginLogContext.Current?.CorrelationId != id)
                    Interlocked.Increment(ref failures);
            });
        }

        await Task.WhenAll(workTasks);
        Assert.Equal(0, failures);
    }

    // ------------------------------------------------------------------ //
    // 8. Dispose is idempotent
    // ------------------------------------------------------------------ //

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var ctx = PluginLogContext.Push("Plugin", "Op");
        ctx.Dispose();
        ctx.Dispose(); // should not throw or corrupt state
        Assert.Null(PluginLogContext.Current);
    }

    // ------------------------------------------------------------------ //
    // 9. Argument validation
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData(null, "Op")]
    [InlineData("", "Op")]
    [InlineData("   ", "Op")]
    public void Push_WithBlankPluginName_Throws(string? pluginName, string op)
    {
        Assert.Throws<ArgumentException>(() => PluginLogContext.Push(pluginName!, op));
    }

    [Theory]
    [InlineData("Plugin", null)]
    [InlineData("Plugin", "")]
    [InlineData("Plugin", "   ")]
    public void Push_WithBlankOperation_Throws(string pluginName, string? op)
    {
        Assert.Throws<ArgumentException>(() => PluginLogContext.Push(pluginName, op!));
    }
}
