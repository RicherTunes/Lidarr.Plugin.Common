// <copyright file="StreamingTimeoutPolicyTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using Lidarr.Plugin.Common.Streaming;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

public class StreamingTimeoutPolicyTests
{
    [Fact]
    public void Default_HasReasonableValues()
    {
        // Act
        var policy = StreamingTimeoutPolicy.Default;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.TotalStreamTimeout);
    }

    [Fact]
    public void ForLocalProvider_HasShorterTimeouts()
    {
        // Act
        var policy = StreamingTimeoutPolicy.ForLocalProvider();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(10), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), policy.TotalStreamTimeout);
    }

    [Fact]
    public void ForCloudProvider_HasLongerTimeouts()
    {
        // Act
        var policy = StreamingTimeoutPolicy.ForCloudProvider();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.TotalStreamTimeout);
    }

    [Fact]
    public void ForClaudeCode_HasAppropriateTimeouts()
    {
        // Act
        var policy = StreamingTimeoutPolicy.ForClaudeCode();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(45), policy.FirstChunkTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.InterChunkTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.TotalStreamTimeout);
    }

    [Fact]
    public void Validate_ValidPolicy_DoesNotThrow()
    {
        // Arrange
        var policy = StreamingTimeoutPolicy.Default;

        // Act & Assert - should not throw
        policy.Validate();
    }

    [Fact]
    public void Validate_ZeroFirstChunkTimeout_Throws()
    {
        // Arrange
        var policy = new StreamingTimeoutPolicy
        {
            FirstChunkTimeout = TimeSpan.Zero,
            InterChunkTimeout = TimeSpan.FromSeconds(5),
            TotalStreamTimeout = TimeSpan.FromMinutes(1),
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
        Assert.Contains("FirstChunkTimeout", ex.Message);
    }

    [Fact]
    public void Validate_NegativeInterChunkTimeout_Throws()
    {
        // Arrange
        var policy = new StreamingTimeoutPolicy
        {
            FirstChunkTimeout = TimeSpan.FromSeconds(5),
            InterChunkTimeout = TimeSpan.FromSeconds(-1),
            TotalStreamTimeout = TimeSpan.FromMinutes(1),
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
        Assert.Contains("InterChunkTimeout", ex.Message);
    }

    [Fact]
    public void Validate_NegativeTotalStreamTimeout_Throws()
    {
        // Arrange
        var policy = new StreamingTimeoutPolicy
        {
            FirstChunkTimeout = TimeSpan.FromSeconds(5),
            InterChunkTimeout = TimeSpan.FromSeconds(5),
            TotalStreamTimeout = TimeSpan.FromMinutes(-1),
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
        Assert.Contains("TotalStreamTimeout", ex.Message);
    }

    [Fact]
    public void WithExpression_ModifiesOnlySpecifiedProperty()
    {
        // Arrange
        var original = StreamingTimeoutPolicy.Default;

        // Act
        var modified = original with { FirstChunkTimeout = TimeSpan.FromSeconds(60) };

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), modified.FirstChunkTimeout);
        Assert.Equal(original.InterChunkTimeout, modified.InterChunkTimeout);
        Assert.Equal(original.TotalStreamTimeout, modified.TotalStreamTimeout);
    }
}
