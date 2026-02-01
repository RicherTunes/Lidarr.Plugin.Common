// <copyright file="SseFramingReader.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Lidarr.Plugin.Common.Streaming;

/// <summary>
/// Reads raw Server-Sent Events (SSE) framing from a stream.
/// This reader handles the SSE wire protocol only (data/event/id/retry fields),
/// without parsing the payload content.
/// </summary>
/// <remarks>
/// <para>
/// SSE specification: https://html.spec.whatwg.org/multipage/server-sent-events.html
/// </para>
/// <para>
/// Each event consists of one or more fields followed by a blank line.
/// Fields are: data, event, id, retry. Multiple data fields are concatenated with newlines.
/// </para>
/// </remarks>
public sealed class SseFramingReader
{
    /// <summary>
    /// Default maximum event size (1 MB).
    /// </summary>
    public const int DefaultMaxEventSize = 1024 * 1024;

    private readonly Stream _stream;
    private readonly Encoding _encoding;
    private readonly int _bufferSize;
    private readonly int _maxEventSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="SseFramingReader"/> class.
    /// </summary>
    /// <param name="stream">The stream to read SSE data from.</param>
    /// <param name="encoding">The text encoding (default: UTF-8).</param>
    /// <param name="bufferSize">Internal buffer size (default: 8KB).</param>
    /// <param name="maxEventSize">Maximum allowed event size in bytes (default: 1MB). Set to 0 for unlimited.</param>
    public SseFramingReader(Stream stream, Encoding? encoding = null, int bufferSize = 8192, int maxEventSize = DefaultMaxEventSize)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _encoding = encoding ?? Encoding.UTF8;
        _bufferSize = bufferSize > 0 ? bufferSize : throw new ArgumentOutOfRangeException(nameof(bufferSize));
        _maxEventSize = maxEventSize >= 0 ? maxEventSize : throw new ArgumentOutOfRangeException(nameof(maxEventSize));
    }

    /// <summary>
    /// Reads SSE frames from the stream asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of SSE frames.</returns>
    public async IAsyncEnumerable<SseFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(_stream, _encoding, detectEncodingFromByteOrderMarks: false, _bufferSize, leaveOpen: true);
        var currentFrame = new SseFrameBuilder(_maxEventSize);
        var hasData = false;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            // Blank line signals end of event
            if (string.IsNullOrEmpty(line))
            {
                if (hasData)
                {
                    yield return currentFrame.Build();
                    currentFrame = new SseFrameBuilder(_maxEventSize);
                    hasData = false;
                }

                continue;
            }

            // Skip comments
            if (line.StartsWith(':'))
            {
                continue;
            }

            // Parse field:value
            var colonIndex = line.IndexOf(':');
            string fieldName;
            string fieldValue;

            if (colonIndex == -1)
            {
                // Field name only, empty value
                fieldName = line;
                fieldValue = string.Empty;
            }
            else
            {
                fieldName = line[..colonIndex];
                // Skip single space after colon if present
                var valueStart = colonIndex + 1;
                if (valueStart < line.Length && line[valueStart] == ' ')
                {
                    valueStart++;
                }

                fieldValue = line[valueStart..];
            }

            switch (fieldName)
            {
                case "data":
                    currentFrame.AppendData(fieldValue);
                    hasData = true;
                    break;

                case "event":
                    currentFrame.EventType = fieldValue;
                    break;

                case "id":
                    currentFrame.Id = fieldValue;
                    break;

                case "retry":
                    if (int.TryParse(fieldValue, out var retryMs))
                    {
                        currentFrame.RetryMilliseconds = retryMs;
                    }

                    break;

                // Unknown fields are ignored per spec
            }
        }

        // Handle case where stream ends without final blank line
        if (hasData)
        {
            yield return currentFrame.Build();
        }
    }

    /// <summary>
    /// Helper class to build SSE frames with multiple data fields.
    /// </summary>
    private sealed class SseFrameBuilder
    {
        private readonly StringBuilder _data = new();
        private readonly int _maxEventSize;
        private bool _hasData;
        private int _currentSize;

        public SseFrameBuilder(int maxEventSize = 0)
        {
            _maxEventSize = maxEventSize;
        }

        public string? EventType { get; set; }
        public string? Id { get; set; }
        public int? RetryMilliseconds { get; set; }

        public void AppendData(string value)
        {
            var additionalSize = value.Length + (_hasData ? 1 : 0);

            if (_maxEventSize > 0 && _currentSize + additionalSize > _maxEventSize)
            {
                throw new StreamFrameTooLargeException(_maxEventSize, _currentSize + additionalSize);
            }

            if (_hasData)
            {
                _data.Append('\n');
            }

            _data.Append(value);
            _hasData = true;
            _currentSize += additionalSize;
        }

        public SseFrame Build()
        {
            return new SseFrame
            {
                Data = _data.ToString(),
                EventType = EventType,
                Id = Id,
                RetryMilliseconds = RetryMilliseconds,
            };
        }
    }
}

/// <summary>
/// Represents a single Server-Sent Event frame.
/// </summary>
public readonly record struct SseFrame
{
    /// <summary>
    /// Gets the data payload of the event.
    /// Multiple data fields are concatenated with newlines.
    /// </summary>
    public string Data { get; init; }

    /// <summary>
    /// Gets the event type, if specified.
    /// Defaults to "message" when not specified per SSE spec.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Gets the last event ID, if specified.
    /// Used for reconnection to resume from last received event.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Gets the reconnection time in milliseconds, if specified.
    /// Instructs the client to wait this long before reconnecting.
    /// </summary>
    public int? RetryMilliseconds { get; init; }

    /// <summary>
    /// Gets a value indicating whether this frame signals the end of the stream.
    /// Convention: data: [DONE] is used by many APIs.
    /// </summary>
    public bool IsDone => Data == "[DONE]";
}
