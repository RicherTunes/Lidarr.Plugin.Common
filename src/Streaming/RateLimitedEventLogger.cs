// <copyright file="RateLimitedEventLogger.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;

namespace Lidarr.Plugin.Common.Streaming;

/// <summary>
/// Provides rate-limited logging for streaming events to prevent log spam.
/// Logs the first N unique events, then summarizes suppressed events at the end.
/// </summary>
public sealed class RateLimitedEventLogger
{
    private readonly Action<string> _logAction;
    private readonly int _maxUniqueEvents;
    private readonly HashSet<string> _loggedEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _suppressedCounts = new(StringComparer.OrdinalIgnoreCase);
    private int _totalSuppressed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitedEventLogger"/> class.
    /// </summary>
    /// <param name="logAction">The action to invoke for logging.</param>
    /// <param name="maxUniqueEvents">Maximum number of unique event types to log (default: 3).</param>
    public RateLimitedEventLogger(Action<string> logAction, int maxUniqueEvents = 3)
    {
        _logAction = logAction ?? throw new ArgumentNullException(nameof(logAction));
        _maxUniqueEvents = maxUniqueEvents > 0 ? maxUniqueEvents : throw new ArgumentOutOfRangeException(nameof(maxUniqueEvents));
    }

    /// <summary>
    /// Gets the total number of suppressed log entries.
    /// </summary>
    public int TotalSuppressed => _totalSuppressed;

    /// <summary>
    /// Gets the number of unique event types that were logged.
    /// </summary>
    public int UniqueEventsLogged => _loggedEvents.Count;

    /// <summary>
    /// Logs an unknown event type, with rate limiting.
    /// </summary>
    /// <param name="eventType">The unknown event type.</param>
    /// <param name="message">The message to log.</param>
    public void LogUnknownEvent(string eventType, string message)
    {
        if (_loggedEvents.Count < _maxUniqueEvents)
        {
            if (_loggedEvents.Add(eventType))
            {
                _logAction(message);
            }
            else
            {
                // Already logged this event type, suppress
                Interlocked.Increment(ref _totalSuppressed);
                IncrementSuppressedCount(eventType);
            }
        }
        else
        {
            // At max unique events, suppress
            Interlocked.Increment(ref _totalSuppressed);
            IncrementSuppressedCount(eventType);
        }
    }

    /// <summary>
    /// Logs a summary of suppressed events. Call this at the end of stream processing.
    /// </summary>
    public void LogSuppressedSummary()
    {
        if (_totalSuppressed > 0)
        {
            var summary = $"Suppressed {_totalSuppressed} additional unknown event(s). " +
                          $"Unique types seen: {string.Join(", ", _suppressedCounts.Keys)}";
            _logAction(summary);
        }
    }

    /// <summary>
    /// Creates an action suitable for passing to ClaudeCodeStreamParser.
    /// </summary>
    /// <returns>An action that rate-limits logging of unknown events.</returns>
    public Action<string> CreateLoggerAction()
    {
        return message =>
        {
            // Extract event type from message if possible (format: "Unknown Claude stream event type: 'xxx'...")
            var eventType = ExtractEventType(message) ?? "unknown";
            LogUnknownEvent(eventType, message);
        };
    }

    private void IncrementSuppressedCount(string eventType)
    {
        lock (_suppressedCounts)
        {
            _suppressedCounts.TryGetValue(eventType, out var count);
            _suppressedCounts[eventType] = count + 1;
        }
    }

    private static string? ExtractEventType(string message)
    {
        // Look for pattern: "Unknown Claude stream event type: 'xxx'"
        const string prefix = "event type: '";
        var startIdx = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
        {
            return null;
        }

        startIdx += prefix.Length;
        var endIdx = message.IndexOf('\'', startIdx);
        if (endIdx < 0)
        {
            return null;
        }

        return message[startIdx..endIdx];
    }
}
