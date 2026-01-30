// <copyright file="OpenAiStreamDecoderContractTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.IO;
using System.Text;
using Lidarr.Plugin.Common.Streaming;
using Lidarr.Plugin.Common.Streaming.Decoders;

namespace Lidarr.Plugin.Common.Tests.Streaming;

/// <summary>
/// Contract tests for OpenAI stream decoder.
/// </summary>
public class OpenAiStreamDecoderContractTests : StreamContractTestsBase
{
    protected override string ExpectedContentType => "text/event-stream";
    protected override string ProviderId => "openai";

    protected override IStreamDecoder CreateDecoder() => new OpenAiStreamDecoder();

    protected override Stream CreateDoneTerminatedStream()
    {
        var content = @"data: {""choices"":[{""delta"":{""content"":""Hello""}}]}

data: [DONE]

";
        return CreateStream(content);
    }

    protected override Stream CreateFinishReasonTerminatedStream()
    {
        var content = @"data: {""choices"":[{""delta"":{""content"":""Hello""},""finish_reason"":""stop""}]}

";
        return CreateStream(content);
    }

    protected override Stream CreateMultipleChunksStream(int chunkCount)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunkCount; i++)
        {
            sb.AppendLine($@"data: {{""choices"":[{{""delta"":{{""content"":""chunk{i}""}}}}]}}");
            sb.AppendLine();
        }

        sb.AppendLine("data: [DONE]");
        sb.AppendLine();
        return CreateStream(sb.ToString());
    }

    protected override Stream CreateMalformedStream()
    {
        var content = @"data: not valid json

data: {missing closing brace

data: {""choices"":[{""delta"":{""content"":""valid""}}]}

data: [DONE]

";
        return CreateStream(content);
    }
}
