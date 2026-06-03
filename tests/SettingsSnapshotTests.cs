using System;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// SettingsSnapshot.Copy reflection-copies every read-write property — the structural fix for the
    /// cross-plugin "snapshot silently dropped a field" bug class (amazon EnableDrm, tidal SaveSyncedLyrics /
    /// UseLRCLIB). A newly-added property is captured automatically; get-only properties are skipped.
    /// </summary>
    public sealed class SettingsSnapshotTests
    {
        private sealed class SampleSettings
        {
            public string? ConfigPath { get; set; }
            public int MaxConcurrent { get; set; }
            public bool EnableFeature { get; set; }
            public SampleQuality Quality { get; set; }

            // Get-only / computed — must NOT be touched by the copy.
            public string Computed => $"{ConfigPath}:{MaxConcurrent}";
        }

        private enum SampleQuality { Sd, Hd, UltraHd }

        [Fact]
        public void Copy_CopiesEveryReadWriteProperty()
        {
            var source = new SampleSettings
            {
                ConfigPath = "/cfg",
                MaxConcurrent = 7,
                EnableFeature = true,
                Quality = SampleQuality.UltraHd,
            };

            var copy = SettingsSnapshot.Copy(source);

            Assert.NotSame(source, copy);
            Assert.Equal("/cfg", copy.ConfigPath);
            Assert.Equal(7, copy.MaxConcurrent);
            Assert.True(copy.EnableFeature);
            Assert.Equal(SampleQuality.UltraHd, copy.Quality);
        }

        [Fact]
        public void Copy_IsIndependentOfSource()
        {
            var source = new SampleSettings { ConfigPath = "/a", MaxConcurrent = 1 };
            var copy = SettingsSnapshot.Copy(source);

            source.ConfigPath = "/changed";
            source.MaxConcurrent = 99;

            Assert.Equal("/a", copy.ConfigPath);
            Assert.Equal(1, copy.MaxConcurrent);
        }

        [Fact]
        public void Copy_IgnoresGetOnlyProperties_DoesNotThrow()
        {
            var copy = SettingsSnapshot.Copy(new SampleSettings { ConfigPath = "/x", MaxConcurrent = 2 });

            // Computed has no setter; it is derived from the copied fields, never assigned.
            Assert.Equal("/x:2", copy.Computed);
        }

        [Fact]
        public void Copy_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SettingsSnapshot.Copy<SampleSettings>(null!));
        }
    }
}
