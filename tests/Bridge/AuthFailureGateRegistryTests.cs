using System;
using System.Linq;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

// AuthFailureGateRegistry is deprecated (Wave-26 audit — zero non-test consumers).
// The tests are retained to document the contract for future reference and to
// guard against accidental silent removal before the v2.0.0 scheduled deletion.
#pragma warning disable CS0618

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="IAuthFailureGateRegistry"/> — the per-key gate
/// store used by multi-provider plugins (brainarr's 11 LLM providers).
/// Driver: a single global AuthFailureGate is the wrong shape for plugins
/// where multiple independent credentials exist. One bad OpenAI key should
/// not gate Anthropic or Ollama. The registry hands out per-key gates and
/// isolates failure state across keys.
/// </summary>
public sealed class AuthFailureGateRegistryTests
{
    private static AuthFailureGateRegistry NewRegistry(TimeSpan? probeInterval = null)
        => new AuthFailureGateRegistry(
            TimeProvider.System,
            probeInterval ?? TimeSpan.FromSeconds(60),
            NullLoggerFactory.Instance);

    [Fact]
    public void Get_ReturnsSameGateInstance_ForSameKey()
    {
        var registry = NewRegistry();

        var g1 = registry.Get("openai");
        var g2 = registry.Get("openai");

        Assert.Same(g1, g2);
    }

    [Fact]
    public void Get_ReturnsDistinctGates_ForDifferentKeys()
    {
        var registry = NewRegistry();

        var openai = registry.Get("openai");
        var anthropic = registry.Get("anthropic");

        Assert.NotSame(openai, anthropic);
        Assert.NotSame(openai.Handler, anthropic.Handler);
    }

    [Fact]
    public async Task FailureOnOneKey_DoesNotLatchOtherKey()
    {
        // The headline behavior: a bad OpenAI key must not block Anthropic.
        var registry = NewRegistry();
        var openai = registry.Get("openai");
        var anthropic = registry.Get("anthropic");

        await openai.Handler.HandleFailureAsync(new AuthFailure { Message = "OpenAI bad" });

        Assert.False(openai.IsHealthy);
        Assert.True(anthropic.IsHealthy);
    }

    [Fact]
    public async Task ProbeSlotsAreIndependentAcrossKeys()
    {
        var registry = NewRegistry();
        var openai = registry.Get("openai");
        var anthropic = registry.Get("anthropic");

        await openai.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });
        await anthropic.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });

        // Each gate has its own probe budget.
        Assert.True(openai.TryAcquireProbeSlot());
        Assert.False(openai.TryAcquireProbeSlot());
        // Anthropic's slot is untouched.
        Assert.True(anthropic.TryAcquireProbeSlot());
        Assert.False(anthropic.TryAcquireProbeSlot());
    }

    [Fact]
    public void Keys_ListsRegisteredProviders()
    {
        var registry = NewRegistry();
        _ = registry.Get("openai");
        _ = registry.Get("anthropic");
        _ = registry.Get("ollama");

        var keys = registry.Keys.OrderBy(k => k).ToArray();

        Assert.Equal(new[] { "anthropic", "ollama", "openai" }, keys);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        // Provider ids should not be case-sensitive — "OpenAI" and "openai"
        // refer to the same provider.
        var registry = NewRegistry();

        var lower = registry.Get("openai");
        var mixed = registry.Get("OpenAI");

        Assert.Same(lower, mixed);
    }

    [Fact]
    public void Get_RejectsNullOrWhitespaceKey()
    {
        var registry = NewRegistry();

        Assert.Throws<ArgumentException>(() => registry.Get(null!));
        Assert.Throws<ArgumentException>(() => registry.Get(""));
        Assert.Throws<ArgumentException>(() => registry.Get("   "));
    }

    [Fact]
    public async Task RecoveryOnOneKey_DoesNotAffectOtherKey()
    {
        var registry = NewRegistry();
        var openai = registry.Get("openai");
        var anthropic = registry.Get("anthropic");

        await openai.Handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        await anthropic.Handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        await openai.Handler.HandleSuccessAsync(); // user re-credentialed OpenAI

        Assert.True(openai.IsHealthy);
        Assert.False(anthropic.IsHealthy); // anthropic still latched
    }

    [Fact]
    public void TotalCount_ReflectsDistinctKeys()
    {
        var registry = NewRegistry();
        _ = registry.Get("openai");
        _ = registry.Get("openai"); // same key, no new gate
        _ = registry.Get("anthropic");

        Assert.Equal(2, registry.Count);
    }

    // ─── R2-4: Reset(key) and Remove(key) ────────────────────────────────

    [Fact]
    public async Task Reset_OnLatchedKey_ClearsAuthStateToUnknown()
    {
        // Settings-UI "Test Connection" use case: user updated their OpenAI
        // key, wants to retry without waiting for the next probe interval.
        var registry = NewRegistry();
        var openai = registry.Get("openai");
        await openai.Handler.HandleFailureAsync(new AuthFailure { Message = "old key bad" });
        Assert.False(openai.IsHealthy);

        registry.Reset("openai");

        Assert.True(openai.IsHealthy);                  // status flipped back to Unknown/Authenticated
        Assert.Same(openai, registry.Get("openai"));    // SAME gate instance — references held by call sites stay valid
        Assert.True(openai.TryAcquireProbeSlot());      // probe budget also re-armed
    }

    [Fact]
    public void Reset_OnUnknownKey_IsNoOp()
    {
        var registry = NewRegistry();
        // Calling Reset on a key that was never registered must not allocate
        // a new gate (registry.Count stays 0) and must not throw.
        registry.Reset("never-registered");
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Remove_DropsKey_AndReplacesGateOnNextGet()
    {
        var registry = NewRegistry();
        var first = registry.Get("openai");
        await first.Handler.HandleFailureAsync(new AuthFailure { Message = "bad" });

        var removed = registry.Remove("openai");

        Assert.True(removed);
        Assert.Equal(0, registry.Count);

        // After Remove, the next Get returns a FRESH gate (different instance).
        var second = registry.Get("openai");
        Assert.NotSame(first, second);
        Assert.True(second.IsHealthy); // fresh gate starts at Unknown
    }

    [Fact]
    public void Remove_OnUnknownKey_ReturnsFalse()
    {
        var registry = NewRegistry();
        Assert.False(registry.Remove("never-registered"));
    }

    // ─── R2-5: bounded growth ────────────────────────────────────────────

    [Fact]
    public void Get_PastMaxKeyCount_Throws()
    {
        // Defense against misconfigured callers passing dynamic keys (e.g.
        // accidentally including a request id). Without a cap, the registry
        // leaks gates forever. A bounded cap fails loudly the first time
        // the contract is violated.
        var registry = new AuthFailureGateRegistry(
            TimeProvider.System,
            TimeSpan.FromSeconds(60),
            NullLoggerFactory.Instance,
            maxKeys: 3);

        _ = registry.Get("a");
        _ = registry.Get("b");
        _ = registry.Get("c");

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Get("d"));
        Assert.Contains("max", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Get_WithinMaxKeyCount_DoesNotThrow()
    {
        var registry = new AuthFailureGateRegistry(
            TimeProvider.System,
            TimeSpan.FromSeconds(60),
            NullLoggerFactory.Instance,
            maxKeys: 3);

        _ = registry.Get("a");
        _ = registry.Get("b");
        _ = registry.Get("c");
        _ = registry.Get("a"); // re-get is not a new key, allowed
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void Get_DefaultMaxKeys_IsLarge()
    {
        // Sanity: the default cap must not be so low that a real multi-
        // provider plugin (~20 providers) breaks. Spot-check with 50.
        var registry = NewRegistry();
        for (var i = 0; i < 50; i++)
        {
            _ = registry.Get($"provider-{i}");
        }
        Assert.Equal(50, registry.Count);
    }
}
