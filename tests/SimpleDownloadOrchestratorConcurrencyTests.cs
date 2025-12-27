using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Tests for SimpleDownloadOrchestrator maxConcurrentTracks feature.
/// </summary>
public class SimpleDownloadOrchestratorConcurrencyTests
{
    [Theory]
    [InlineData(0, 1)]   // Zero clamps to 1
    [InlineData(-5, 1)]  // Negative clamps to 1
    [InlineData(1, 1)]   // 1 stays 1
    [InlineData(5, 5)]   // Positive values preserved
    public void Constructor_ClampsMaxConcurrentTracks(int input, int expected)
    {
        // Arrange & Act
        var orchestrator = new SimpleDownloadOrchestrator(
            serviceName: "test",
            httpClient: new HttpClient(),
            getAlbumAsync: _ => Task.FromResult<StreamingAlbum>(null!),
            getTrackAsync: _ => Task.FromResult<StreamingTrack>(null!),
            getAlbumTrackIdsAsync: _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            getStreamAsync: (_, _) => Task.FromResult<(string, string)>(("url", "flac")),
            maxConcurrentTracks: input);

        // Assert - verify via reflection that the field was clamped
        var field = typeof(SimpleDownloadOrchestrator).GetField("_maxConcurrentTracks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var actual = (int)field.GetValue(orchestrator)!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Constructor_DefaultMaxConcurrentTracks_IsOne()
    {
        // Arrange & Act - use constructor without maxConcurrentTracks
        var orchestrator = new SimpleDownloadOrchestrator(
            serviceName: "test",
            httpClient: new HttpClient(),
            getAlbumAsync: _ => Task.FromResult<StreamingAlbum>(null!),
            getTrackAsync: _ => Task.FromResult<StreamingTrack>(null!),
            getAlbumTrackIdsAsync: _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            getStreamAsync: (_, _) => Task.FromResult<(string, string)>(("url", "flac")));

        // Assert
        var field = typeof(SimpleDownloadOrchestrator).GetField("_maxConcurrentTracks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var actual = (int)field.GetValue(orchestrator)!;
        Assert.Equal(1, actual);
    }
}
