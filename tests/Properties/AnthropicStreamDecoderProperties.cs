// <copyright file="AnthropicStreamDecoderProperties.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Streaming.Decoders;

namespace Lidarr.Plugin.Common.Tests.Properties
{
    /// <summary>
    /// FsCheck property tests for <see cref="AnthropicStreamDecoder"/>.
    /// Wave 28 extension: validates content reconstruction, malformed-frame skip
    /// robustness, and message_stop termination via deterministic SSE builders.
    /// </summary>
    public class AnthropicStreamDecoderProperties
    {
        private static readonly AnthropicStreamDecoder Decoder = new();

        /// <summary>
        /// Build a deterministic ASCII text token from a single byte (avoids JSON-escaping
        /// concerns: alphanum + space only). Length 1..16 chars.
        /// </summary>
        private static string TextFromByte(byte b)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";
            var len = (b % 16) + 1;
            var sb = new StringBuilder(len);
            for (var i = 0; i < len; i++)
            {
                sb.Append(alphabet[(b + i) % alphabet.Length]);
            }
            return sb.ToString();
        }

        private static byte[] BuildSse(IEnumerable<string> texts, bool includeStop = true)
        {
            var sb = new StringBuilder();
            sb.Append("event: message_start\n")
              .Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\n");
            foreach (var t in texts)
            {
                sb.Append("event: content_block_delta\n")
                  .Append("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"")
                  .Append(t)
                  .Append("\"}}\n\n");
            }

            if (includeStop)
            {
                sb.Append("event: message_stop\n")
                  .Append("data: {\"type\":\"message_stop\"}\n\n");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static async Task<LlmStreamChunk[]> CollectAsync(byte[] sse)
        {
            using var stream = new MemoryStream(sse);
            var list = new List<LlmStreamChunk>();
            await foreach (var chunk in Decoder.DecodeAsync(stream))
            {
                list.Add(chunk);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Content reconstruction: concatenated <c>ContentDelta</c> across all non-terminal
        /// chunks equals the synthesized concatenation of the input text deltas.
        /// </summary>
        [Property(MaxTest = 25)]
        public Property ContentDelta_Concatenation_EqualsInput(byte[] tokens)
        {
            tokens ??= Array.Empty<byte>();
            // Cap to keep individual iterations cheap.
            var capped = tokens.Take(16).ToArray();
            var texts = capped.Select(TextFromByte).ToArray();
            var expected = string.Concat(texts);

            var sse = BuildSse(texts);
            var chunks = CollectAsync(sse).GetAwaiter().GetResult();

            var actual = string.Concat(chunks
                .Where(c => c.ContentDelta != null)
                .Select(c => c.ContentDelta));

            var lastIsTerminal = chunks.Length > 0 && chunks[^1].IsComplete;
            return (actual == expected && lastIsTerminal).ToProperty();
        }

        /// <summary>
        /// Malformed-frame robustness: inserting a frame with non-JSON data between valid
        /// frames does NOT lose any valid content; the bad frame is silently skipped.
        /// </summary>
        [Property(MaxTest = 20)]
        public Property MalformedFrame_Skipped_ValidContentPreserved(byte[] tokens, byte rawInsertAt)
        {
            tokens ??= Array.Empty<byte>();
            var capped = tokens.Take(8).ToArray();
            if (capped.Length == 0) capped = new byte[] { 1 };
            var texts = capped.Select(TextFromByte).ToArray();
            var expected = string.Concat(texts);

            var insertAt = rawInsertAt % (texts.Length + 1);

            var sb = new StringBuilder();
            sb.Append("event: message_start\n")
              .Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\n");

            for (var i = 0; i < texts.Length; i++)
            {
                if (i == insertAt)
                {
                    sb.Append("event: content_block_delta\n")
                      .Append("data: this-is-not-valid-json-{{\n\n");
                }

                sb.Append("event: content_block_delta\n")
                  .Append("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"")
                  .Append(texts[i])
                  .Append("\"}}\n\n");
            }

            if (insertAt == texts.Length)
            {
                sb.Append("event: content_block_delta\n")
                  .Append("data: still-not-json\n\n");
            }

            sb.Append("event: message_stop\n")
              .Append("data: {\"type\":\"message_stop\"}\n\n");

            var chunks = CollectAsync(Encoding.UTF8.GetBytes(sb.ToString())).GetAwaiter().GetResult();
            var actual = string.Concat(chunks.Where(c => c.ContentDelta != null).Select(c => c.ContentDelta));

            return (actual == expected && chunks[^1].IsComplete).ToProperty();
        }

        /// <summary>
        /// Termination: a <c>message_stop</c> frame ends the enumeration even if more frames
        /// follow in the underlying stream. The "after stop" payload must NOT appear in the
        /// reconstructed content.
        /// </summary>
        [Property(MaxTest = 20)]
        public Property MessageStop_Terminates_IgnoresTrailingFrames(byte[] beforeTokens, byte[] afterTokens)
        {
            beforeTokens ??= Array.Empty<byte>();
            afterTokens ??= Array.Empty<byte>();
            var before = beforeTokens.Take(8).Select(TextFromByte).ToArray();
            var after = afterTokens.Take(8).Select(TextFromByte).ToArray();
            if (before.Length == 0) before = new[] { "X" };
            if (after.Length == 0) after = new[] { "Y" };

            // Build: before-deltas, message_stop, after-deltas (which must be ignored).
            var sb = new StringBuilder();
            sb.Append("event: message_start\n")
              .Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\n");
            foreach (var t in before)
            {
                sb.Append("event: content_block_delta\n")
                  .Append("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"")
                  .Append(t).Append("\"}}\n\n");
            }
            sb.Append("event: message_stop\n")
              .Append("data: {\"type\":\"message_stop\"}\n\n");
            foreach (var t in after)
            {
                sb.Append("event: content_block_delta\n")
                  .Append("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"")
                  .Append(t).Append("\"}}\n\n");
            }

            var chunks = CollectAsync(Encoding.UTF8.GetBytes(sb.ToString())).GetAwaiter().GetResult();
            var actual = string.Concat(chunks.Where(c => c.ContentDelta != null).Select(c => c.ContentDelta));
            var expected = string.Concat(before);

            // actual==expected is the strongest invariant: equality means no leakage from
            // "after" frames AND no truncation of "before" frames. The terminal chunk must
            // also be flagged complete (the message_stop branch yields IsComplete=true).
            return (actual == expected && chunks[^1].IsComplete).ToProperty();
        }
    }
}
