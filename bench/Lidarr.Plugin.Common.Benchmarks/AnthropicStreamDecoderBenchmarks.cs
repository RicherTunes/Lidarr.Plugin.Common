using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Lidarr.Plugin.Common.Streaming.Decoders;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Baseline for <see cref="AnthropicStreamDecoder.DecodeAsync"/> over a representative
/// SSE message — message_start, a sequence of content_block_delta frames, message_delta
/// (with usage), and message_stop. Two payload sizes cover the typical short-reply and
/// chat-message envelopes seen by brainarr's Anthropic provider.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class AnthropicStreamDecoderBenchmarks
{
    private byte[] _smallPayload = null!;
    private byte[] _typicalPayload = null!;
    private AnthropicStreamDecoder _decoder = null!;

    [GlobalSetup]
    public void Setup()
    {
        _decoder = new AnthropicStreamDecoder();
        _smallPayload = Encoding.UTF8.GetBytes(BuildSse(deltaCount: 4, wordsPerDelta: 3));
        _typicalPayload = Encoding.UTF8.GetBytes(BuildSse(deltaCount: 32, wordsPerDelta: 8));
    }

    [Benchmark(Description = "DecodeAsync — small message (~4 deltas)")]
    public async Task<int> SmallMessage()
    {
        return await ConsumeAsync(_smallPayload).ConfigureAwait(false);
    }

    [Benchmark(Description = "DecodeAsync — typical message (~32 deltas)")]
    public async Task<int> TypicalMessage()
    {
        return await ConsumeAsync(_typicalPayload).ConfigureAwait(false);
    }

    private async Task<int> ConsumeAsync(byte[] payload)
    {
        using var ms = new MemoryStream(payload, writable: false);
        int count = 0;
        await foreach (var _ in _decoder.DecodeAsync(ms).ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }

    private static string BuildSse(int deltaCount, int wordsPerDelta)
    {
        var sb = new StringBuilder(2048);

        sb.Append("event: message_start\n");
        sb.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_bench\",\"usage\":{\"input_tokens\":42,\"output_tokens\":0}}}\n\n");

        var word = "lorem ";
        var deltaText = string.Concat(System.Linq.Enumerable.Repeat(word, wordsPerDelta)).TrimEnd();

        for (int i = 0; i < deltaCount; i++)
        {
            sb.Append("event: content_block_delta\n");
            sb.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"");
            sb.Append(deltaText);
            sb.Append("\"}}\n\n");
        }

        sb.Append("event: message_delta\n");
        sb.Append("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":");
        sb.Append(deltaCount * wordsPerDelta);
        sb.Append("}}\n\n");

        sb.Append("event: message_stop\n");
        sb.Append("data: {\"type\":\"message_stop\"}\n\n");

        return sb.ToString();
    }
}
