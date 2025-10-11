using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Minimal no-op metrics surface to avoid hard dependencies.
/// Plugins can light this up later with real IMeter or exporters.
/// </summary>
public static class Metrics
{
    public static readonly NoopCounter CacheHit = new();
    public static readonly NoopCounter CacheMiss = new();
    public static readonly NoopCounter AuthRefreshes = new();

    public sealed class NoopCounter
    {
        public void Add(long value) { }
        public void Add(long value, params KeyValuePair<string, object?>[] tags) { }
    }
}
