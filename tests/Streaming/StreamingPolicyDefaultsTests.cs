// <copyright file="StreamingPolicyDefaultsTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using Lidarr.Plugin.Common.Streaming;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

/// <summary>
/// Tests that lock down the default timeout values.
/// If these tests fail, you're changing defaults that could affect E2E stability.
/// </summary>
public class StreamingPolicyDefaultsTests
{
    [Fact]
    public void Default_FirstChunkTimeout_Is30Seconds()
    {
        var policy = StreamingTimeoutPolicy.Default;
        Assert.Equal(TimeSpan.FromSeconds(30), policy.FirstChunkTimeout);
    }

    [Fact]
    public void Default_InterChunkTimeout_Is15Seconds()
    {
        var policy = StreamingTimeoutPolicy.Default;
        Assert.Equal(TimeSpan.FromSeconds(15), policy.InterChunkTimeout);
    }

    [Fact]
    public void Default_TotalStreamTimeout_Is5Minutes()
    {
        var policy = StreamingTimeoutPolicy.Default;
        Assert.Equal(TimeSpan.FromMinutes(5), policy.TotalStreamTimeout);
    }

    [Fact]
    public void ForLocalProvider_HasShorterTimeouts()
    {
        var policy = StreamingTimeoutPolicy.ForLocalProvider();

        Assert.Equal(TimeSpan.FromSeconds(10), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), policy.TotalStreamTimeout);
    }

    [Fact]
    public void ForCloudProvider_HasLongerTimeouts()
    {
        var policy = StreamingTimeoutPolicy.ForCloudProvider();

        Assert.Equal(TimeSpan.FromSeconds(60), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.TotalStreamTimeout);
    }

    [Fact]
    public void ForClaudeCode_HasAppropriateTimeouts()
    {
        var policy = StreamingTimeoutPolicy.ForClaudeCode();

        Assert.Equal(TimeSpan.FromSeconds(45), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.TotalStreamTimeout);
    }

    [Fact]
    public void SseFramingReader_DefaultMaxEventSize_Is1MB()
    {
        Assert.Equal(1024 * 1024, SseFramingReader.DefaultMaxEventSize);
    }

    [Fact]
    public void RateLimitedEventLogger_DefaultMaxUniqueEvents_Is3()
    {
        var callCount = 0;
        var logger = new RateLimitedEventLogger(_ => callCount++);

        // Log 5 different event types
        for (int i = 0; i < 5; i++)
        {
            logger.LogUnknownEvent($"event{i}", $"Unknown event: event{i}");
        }

        // Only 3 should be logged (default max)
        Assert.Equal(3, callCount);
        Assert.Equal(2, logger.TotalSuppressed);
    }
}
