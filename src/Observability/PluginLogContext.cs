using System;
using System.Threading;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Lightweight async-local scope for structured plugin log lines.
///
/// Pushes a named context onto an <see cref="AsyncLocal{T}"/> stack so that
/// caller code can format consistent log prefixes without taking a hard
/// dependency on any particular logging framework.
///
/// Usage:
/// <code>
/// using var ctx = PluginLogContext.Push("Tidalarr", "Search", provider: "tidal");
/// logger.Info($"{ctx.LinePrefix()}fetching tracks…");
/// </code>
///
/// Nested pushes stack; disposing a child scope restores the parent.
/// Multiple concurrent <c>async</c> paths each maintain their own scope
/// without bleeding into one another (AsyncLocal semantics).
/// </summary>
public sealed class PluginLogContext : IDisposable
{
    // AsyncLocal slot holds a linked-list node so nested Push/Pop is O(1).
    private static readonly AsyncLocal<PluginLogContext?> _current = new();

    private readonly PluginLogContext? _parent;
    private bool _disposed;

    private PluginLogContext(string pluginName, string operation, string correlationId, string? provider, PluginLogContext? parent)
    {
        PluginName = pluginName;
        Operation = operation;
        CorrelationId = correlationId;
        Provider = provider;
        _parent = parent;
    }

    // ------------------------------------------------------------------ //
    // Factory
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Opens a new log context scope and makes it the current scope on the
    /// calling async execution context. Dispose the returned object to pop.
    /// </summary>
    /// <param name="pluginName">Plugin name, e.g. "Tidalarr".</param>
    /// <param name="operation">Operation name, e.g. "Search" or "Download".</param>
    /// <param name="correlationId">
    ///   Optional correlation ID. When omitted a new <see cref="Guid"/> is used.
    /// </param>
    /// <param name="provider">Optional provider identifier, e.g. "tidal".</param>
    public static PluginLogContext Push(
        string pluginName,
        string operation,
        string? correlationId = null,
        string? provider = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName)) throw new ArgumentException("pluginName must not be empty.", nameof(pluginName));
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("operation must not be empty.", nameof(operation));

        var ctx = new PluginLogContext(
            pluginName,
            operation,
            correlationId ?? Guid.NewGuid().ToString("N"),
            provider,
            _current.Value);

        _current.Value = ctx;
        return ctx;
    }

    // ------------------------------------------------------------------ //
    // Current scope accessor
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The innermost active <see cref="PluginLogContext"/> on the current
    /// async execution context, or <c>null</c> if no scope is active.
    /// </summary>
    public static PluginLogContext? Current => _current.Value;

    // ------------------------------------------------------------------ //
    // Properties
    // ------------------------------------------------------------------ //

    /// <summary>Plugin name, e.g. "Tidalarr".</summary>
    public string PluginName { get; }

    /// <summary>Operation label, e.g. "Search" or "GetRecommendations".</summary>
    public string Operation { get; }

    /// <summary>
    /// Correlation ID for this scope. Defaults to a GUID (no hyphens)
    /// when not supplied to <see cref="Push"/>.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>Optional provider identifier, e.g. "tidal" or "qobuz".</summary>
    public string? Provider { get; }

    // ------------------------------------------------------------------ //
    // Formatting
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns a structured log line prefix in the form
    /// <c>[op:correlationId:provider] </c> (provider segment omitted when null).
    /// Callers interpolate this into their existing logger calls:
    /// <code>
    /// logger.Info($"{ctx.LinePrefix()}fetched {count} tracks");
    /// </code>
    /// </summary>
    public string LinePrefix()
    {
        return Provider is not null
            ? $"[{Operation}:{CorrelationId}:{Provider}] "
            : $"[{Operation}:{CorrelationId}] ";
    }

    // ------------------------------------------------------------------ //
    // IDisposable — pop this scope
    // ------------------------------------------------------------------ //

    /// <summary>Pops this scope and restores the parent (if any).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Only pop if we're still the current scope. If a caller leaked an inner
        // scope without disposing it, we leave the stack in a best-effort state.
        if (ReferenceEquals(_current.Value, this))
            _current.Value = _parent;
    }
}
