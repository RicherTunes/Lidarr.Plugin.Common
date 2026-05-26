using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Lidarr.Plugin.Abstractions.Contracts;

using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Per-key store of <see cref="AuthFailureGate"/> instances. Multi-provider
/// plugins (e.g. brainarr — 11 LLM providers) need independent gates per
/// credential: a bad OpenAI key must not gate Anthropic or Ollama.
/// </summary>
/// <remarks>
/// Each gate owns its own <see cref="IAuthFailureHandler"/> instance, so
/// per-key status / failure detail / probe budget are fully isolated.
/// Keys are matched case-insensitively (provider id "OpenAI" and "openai"
/// refer to the same gate).
///
/// The registry is bounded by <c>maxKeys</c> (default 256) to defend against
/// misconfigured callers passing dynamic keys (e.g. request ids by mistake).
/// </remarks>
/// <remarks>
/// <b>Deprecation (v1.18.0):</b> A Wave-26 adversarial audit found zero non-test
/// plugin consumers across all four ecosystem repos. Every real call-site uses
/// <see cref="AuthFailureGate"/> directly (typically via a hand-rolled
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// keyed per provider, which allows pairing a custom <see cref="IAuthFailureHandler"/>
/// with each gate — something this registry cannot do because it wires
/// <see cref="DefaultAuthFailureHandler"/> internally). This interface and its
/// implementation will be removed in v2.0.0.
/// </remarks>
[Obsolete(
    "AuthFailureGateRegistry has no plugin consumers (Wave-26 audit). " +
    "Manage per-key AuthFailureGate instances directly via ConcurrentDictionary<string, AuthFailureGate> " +
    "so you can pair each gate with a custom IAuthFailureHandler. " +
    "This type will be removed in v2.0.0.",
    error: false)]
public interface IAuthFailureGateRegistry
{
    /// <summary>Get or create the gate for a provider key.</summary>
    /// <exception cref="ArgumentException">key is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">key would exceed the configured max.</exception>
    AuthFailureGate Get(string key);

    /// <summary>
    /// Reset the gate for <paramref name="key"/> to its fully-open initial
    /// state (status Unknown, no latch, fresh probe budget). The SAME gate
    /// instance is preserved — any reference held by a call site stays valid.
    /// No-op when the key is not registered.
    /// </summary>
    void Reset(string key);

    /// <summary>
    /// Drop the gate for <paramref name="key"/>. The next <see cref="Get"/>
    /// returns a fresh instance. Returns true if a gate was removed.
    /// </summary>
    bool Remove(string key);

    /// <summary>Registered keys (case-normalized).</summary>
    IReadOnlyCollection<string> Keys { get; }

    /// <summary>Number of distinct keys with provisioned gates.</summary>
    int Count { get; }
}

[Obsolete(
    "AuthFailureGateRegistry has no plugin consumers (Wave-26 audit). " +
    "Manage per-key AuthFailureGate instances directly via ConcurrentDictionary<string, AuthFailureGate> " +
    "so you can pair each gate with a custom IAuthFailureHandler. " +
    "This type will be removed in v2.0.0.",
    error: false)]
public sealed class AuthFailureGateRegistry : IAuthFailureGateRegistry
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _probeInterval;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _maxKeys;
    private readonly ConcurrentDictionary<string, AuthFailureGate> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public AuthFailureGateRegistry(
        TimeProvider? clock = null,
        TimeSpan? probeInterval = null,
        ILoggerFactory? loggerFactory = null,
        int maxKeys = 256)
    {
        if (maxKeys <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeys), "maxKeys must be positive.");
        }
        _clock = clock ?? TimeProvider.System;
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(60);
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _maxKeys = maxKeys;
    }

    public AuthFailureGate Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Provider key must be non-empty.", nameof(key));
        }

        if (_gates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // Pre-check the cap BEFORE incurring the allocation cost. Racy under
        // concurrent first-time inserts (we may briefly exceed _maxKeys by N
        // concurrent additions) — accept that as the price of a lock-free
        // common path; the cap is a defense-in-depth, not a hard contract.
        if (_gates.Count >= _maxKeys)
        {
            throw new InvalidOperationException(
                $"AuthFailureGateRegistry has reached max {_maxKeys} keys. " +
                "If you expect more providers, raise the cap at construction. " +
                "If you don't, this indicates a misconfigured caller using dynamic keys.");
        }

        return _gates.GetOrAdd(key, k =>
        {
            var handler = new DefaultAuthFailureHandler(
                _loggerFactory.CreateLogger<DefaultAuthFailureHandler>());
            return new AuthFailureGate(
                handler,
                _clock,
                _probeInterval,
                _loggerFactory.CreateLogger<AuthFailureGate>());
        });
    }

    public void Reset(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_gates.TryGetValue(key, out var gate))
        {
            gate.ForceReset();
        }
    }

    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return _gates.TryRemove(key, out _);
    }

    public IReadOnlyCollection<string> Keys => _gates.Keys.ToArray();

    public int Count => _gates.Count;
}
