using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Diagnostics;

/// <summary>
/// Process-scoped, per-key warn-once gate.
/// Tracks which keys have already emitted a warn-level log entry and routes
/// subsequent calls to a debug-level action (or a no-op if none supplied).
/// </summary>
/// <remarks>
/// <para>
/// Both call-site patterns are handled by a single instance type:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Process-global single key</strong> (Qobuzarr wire-up pattern): use a
///       <c>private static readonly WarnOnce _warn = new();</c> field and call
///       <see cref="TryWarn(string, Action, Action?)"/> with a fixed key such as
///       <c>"wireup"</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Per-dimension key</strong> (Brainarr tokenizer-fallback pattern): use the
///       same instance and pass a composite key such as
///       <c>$"{reason}:{normalizedModelKey}"</c>.
///     </description>
///   </item>
/// </list>
/// <para>
/// The class is thread-safe: <see cref="TryWarn(string, Action, Action?)"/> can be
/// called concurrently from multiple threads; exactly one caller wins the TryAdd race
/// and invokes the warn action. All other concurrent callers invoke
/// the debug action (or do nothing).
/// </para>
/// <para>
/// <see cref="Reset"/> is provided for unit-test isolation. Do not call it in
/// production code.
/// </para>
/// </remarks>
public sealed class WarnOnce
{
    private readonly ConcurrentDictionary<string, byte> _seen;

    /// <summary>
    /// Initialises a new <see cref="WarnOnce"/> using ordinal (case-sensitive) key comparison.
    /// </summary>
    public WarnOnce()
    {
        _seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Initialises a new <see cref="WarnOnce"/> with a custom key comparer.
    /// </summary>
    /// <param name="keyComparer">Comparer applied to all keys.</param>
    public WarnOnce(IEqualityComparer<string> keyComparer)
    {
        if (keyComparer is null) throw new ArgumentNullException(nameof(keyComparer));
        _seen = new ConcurrentDictionary<string, byte>(keyComparer);
    }

    /// <summary>
    /// Invokes <paramref name="warnAction"/> the first time this method is called with
    /// <paramref name="key"/>; invokes <paramref name="debugAction"/> on all subsequent
    /// calls with the same key.
    /// </summary>
    /// <param name="key">
    /// Stable identifier for this warning site. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="warnAction">
    /// Action to run on the first occurrence (typically <c>logger.Warn(...)</c>).
    /// </param>
    /// <param name="debugAction">
    /// Optional action to run on subsequent occurrences (typically <c>logger.Debug(...)</c>).
    /// If <see langword="null"/>, subsequent calls are silent.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="warnAction"/> was invoked (first call for
    /// this key); <see langword="false"/> if <paramref name="debugAction"/> was invoked or
    /// the call was silently suppressed.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="key"/> or <paramref name="warnAction"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="key"/> is empty or whitespace.
    /// </exception>
    public bool TryWarn(string key, Action warnAction, Action? debugAction = null)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key cannot be empty or whitespace.", nameof(key));
        if (warnAction is null) throw new ArgumentNullException(nameof(warnAction));

        if (_seen.TryAdd(key, 0))
        {
            warnAction();
            return true;
        }

        debugAction?.Invoke();
        return false;
    }

    /// <summary>
    /// Exception-carrying overload of <see cref="TryWarn(string, Action, Action?)"/>.
    /// Passes the exception to both actions so callers can log it at the appropriate level.
    /// </summary>
    /// <param name="key">Stable identifier for this warning site.</param>
    /// <param name="ex">Exception to forward to the action.</param>
    /// <param name="warnAction">Action receiving the exception on the first occurrence.</param>
    /// <param name="debugAction">
    /// Optional action receiving the exception on subsequent occurrences.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="warnAction"/> was invoked; <see langword="false"/> otherwise.
    /// </returns>
    public bool TryWarn(string key, Exception ex, Action<Exception> warnAction, Action<Exception>? debugAction = null)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key cannot be empty or whitespace.", nameof(key));
        if (ex is null) throw new ArgumentNullException(nameof(ex));
        if (warnAction is null) throw new ArgumentNullException(nameof(warnAction));

        if (_seen.TryAdd(key, 0))
        {
            warnAction(ex);
            return true;
        }

        debugAction?.Invoke(ex);
        return false;
    }

    /// <summary>
    /// Clears all previously seen keys, resetting the instance to its initial state.
    /// </summary>
    /// <remarks>
    /// Intended for unit-test isolation only. Calling this in production code will
    /// cause warn messages to re-fire.
    /// </remarks>
    public void Reset() => _seen.Clear();
}
