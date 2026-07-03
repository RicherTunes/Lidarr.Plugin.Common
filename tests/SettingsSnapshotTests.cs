using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class SettingsSnapshotTests
{
    [Fact]
    public void Copy_CopiesEveryPublicReadWriteNonIndexerProperty()
    {
        var source = new SnapshotSettings
        {
            Enabled = true,
            MaxConcurrentDownloads = 4,
            DownloadPath = "/music",
            OptionalTag = "lossless",
            UpdatedAt = new DateTimeOffset(2026, 7, 2, 10, 30, 0, TimeSpan.Zero)
        };

        SnapshotSettings snapshot = SettingsSnapshot.Copy(source);

        Assert.NotSame(source, snapshot);
        Assert.Equal(source.Enabled, snapshot.Enabled);
        Assert.Equal(source.MaxConcurrentDownloads, snapshot.MaxConcurrentDownloads);
        Assert.Equal(source.DownloadPath, snapshot.DownloadPath);
        Assert.Equal(source.OptionalTag, snapshot.OptionalTag);
        Assert.Equal(source.UpdatedAt, snapshot.UpdatedAt);
    }

    [Fact]
    public void Copy_SkipsGetOnlyAndIndexerProperties()
    {
        var source = new SnapshotSettings
        {
            Enabled = true,
            MaxConcurrentDownloads = 2,
            DownloadPath = "/original"
        };

        SnapshotSettings snapshot = SettingsSnapshot.Copy(source);

        Assert.True(snapshot.Enabled);
        Assert.Equal(2, snapshot.MaxConcurrentDownloads);
        Assert.Equal("/original", snapshot.DownloadPath);
    }

    [Fact]
    public void Copy_CopiesInheritedPublicReadWriteProperties()
    {
        var source = new DerivedSnapshotSettings
        {
            BaseDownloadPath = "/base",
            DerivedName = "derived"
        };

        DerivedSnapshotSettings snapshot = SettingsSnapshot.Copy(source);

        Assert.Equal("/base", snapshot.BaseDownloadPath);
        Assert.Equal("derived", snapshot.DerivedName);
    }

    [Fact]
    public void Copy_PreservesNullPropertyValues()
    {
        var source = new SnapshotSettings
        {
            DownloadPath = "/music",
            OptionalTag = null
        };

        SnapshotSettings snapshot = SettingsSnapshot.Copy(source);

        Assert.Null(snapshot.OptionalTag);
    }

    [Fact]
    public void Copy_SkipsPropertiesWithNonPublicSetters()
    {
        var source = new SnapshotSettings
        {
            Enabled = true,
            DownloadPath = "/music"
        };

        SnapshotSettings snapshot = SettingsSnapshot.Copy(source);

        Assert.True(snapshot.Enabled);
        Assert.Equal("/music", snapshot.DownloadPath);
    }

    [Fact]
    public void Copy_CopiesMutableReferencePropertiesByReference()
    {
        var tags = new List<string> { "lossless" };
        var source = new SnapshotSettings
        {
            Tags = tags
        };

        SnapshotSettings snapshot = SettingsSnapshot.Copy(source);

        Assert.Same(tags, snapshot.Tags);

        tags.Add("hi-res");
        Assert.Equal(new[] { "lossless", "hi-res" }, snapshot.Tags);
    }

    [Fact]
    public void Copy_ThrowsForNullSource()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsSnapshot.Copy<SnapshotSettings>(null!));
    }

    private class BaseSnapshotSettings
    {
        public string BaseDownloadPath { get; set; } = string.Empty;
    }

    private sealed class DerivedSnapshotSettings : BaseSnapshotSettings
    {
        public string DerivedName { get; set; } = string.Empty;
    }

    private sealed class SnapshotSettings
    {
        public bool Enabled { get; set; }
        public int MaxConcurrentDownloads { get; set; }
        public string DownloadPath { get; set; } = string.Empty;
        public string? OptionalTag { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<string>? Tags { get; set; }

        public string GetOnly => throw new InvalidOperationException("Get-only properties must not be read.");

        public string NonPublicSetter
        {
            get => throw new InvalidOperationException("Properties with non-public setters must not be read.");
            private set { }
        }

        public string this[int index]
        {
            get => throw new InvalidOperationException("Indexers must not be read.");
            set => throw new InvalidOperationException("Indexers must not be written.");
        }
    }
}
