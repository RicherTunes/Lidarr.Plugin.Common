using System;
using System.Collections.Generic;
using System.Text.Json;
using Lidarr.Plugin.Common.Hosting.Settings;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hosting
{
    /// <summary>
    /// Wave-2 coverage: <see cref="SettingsBinder"/> (internal, exercised via
    /// InternalsVisibleTo) had zero direct test references prior to this file, despite
    /// being the engine behind StreamingPlugin's Validate/Apply settings pipeline
    /// (see StreamingPlugin.StreamingSettingsProvider.Apply, which calls
    /// SettingsBinder.Populate with no surrounding try/catch).
    ///
    /// These tests cover each supported type conversion via ConvertValue (exercised
    /// indirectly through Populate, since ConvertValue itself is private) and the
    /// malformed-input behavior: Populate now skips any single field it cannot convert
    /// (keeping the property's prior value) rather than throwing past the ISettingsProvider
    /// boundary and aborting the entire settings save, and a null value for a Nullable&lt;T&gt;
    /// field is preserved as null instead of being coerced to default(T).
    /// </summary>
    public class SettingsBinderTests
    {
        private enum SampleEnum
        {
            None = 0,
            First = 1,
            Second = 2
        }

        private sealed class SampleSettings
        {
            public string Name { get; set; } = "default-name";
            public int Count { get; set; } = 1;
            public long BigCount { get; set; } = 1L;
            public bool Enabled { get; set; } = true;
            public double Ratio { get; set; } = 0.5;
            public float FloatRatio { get; set; } = 0.5f;
            public decimal Price { get; set; } = 1.0m;
            public SampleEnum Mode { get; set; } = SampleEnum.None;
            public Guid Identifier { get; set; } = Guid.Empty;
            public TimeSpan Interval { get; set; } = TimeSpan.Zero;
            public DateTime Timestamp { get; set; } = DateTime.MinValue;
            public int? NullableCount { get; set; }

            // Read-only property: must be ignored by GetWritableProperties / ToDictionary round-trip.
            public string ReadOnly => "computed";
        }

        // ---------------------------------------------------------------
        // Round-trip (ToDictionary -> Populate) per supported type
        // ---------------------------------------------------------------

        [Fact]
        public void Clone_RoundTripsAllSupportedPrimitiveTypes()
        {
            var source = new SampleSettings
            {
                Name = "qobuz",
                Count = 42,
                BigCount = 9_000_000_000L,
                Enabled = false,
                Ratio = 3.14,
                FloatRatio = 2.5f,
                Price = 19.99m,
                Mode = SampleEnum.Second,
                Identifier = Guid.NewGuid(),
                Interval = TimeSpan.FromMinutes(5),
                Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                NullableCount = 7
            };

            var clone = SettingsBinder.Clone(source);

            Assert.Equal(source.Name, clone.Name);
            Assert.Equal(source.Count, clone.Count);
            Assert.Equal(source.BigCount, clone.BigCount);
            Assert.Equal(source.Enabled, clone.Enabled);
            Assert.Equal(source.Ratio, clone.Ratio);
            Assert.Equal(source.FloatRatio, clone.FloatRatio);
            Assert.Equal(source.Price, clone.Price);
            Assert.Equal(source.Mode, clone.Mode);
            Assert.Equal(source.Identifier, clone.Identifier);
            Assert.Equal(source.Interval, clone.Interval);
            Assert.Equal(source.Timestamp, clone.Timestamp);
            Assert.Equal(source.NullableCount, clone.NullableCount);
        }

        [Fact]
        public void ToDictionary_ExcludesReadOnlyProperties()
        {
            var source = new SampleSettings();
            var dict = SettingsBinder.ToDictionary(source);

            Assert.DoesNotContain("ReadOnly", dict.Keys);
            Assert.Contains("Name", dict.Keys);
        }

        [Fact]
        public void Populate_StringValues_ConvertToEachPrimitiveType()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
            {
                ["Count"] = "42",
                ["BigCount"] = "9000000000",
                ["Enabled"] = "true",
                ["Ratio"] = "3.14",
                ["Mode"] = "Second",
                ["Identifier"] = Guid.NewGuid().ToString(),
                ["Interval"] = "00:05:00",
                ["Timestamp"] = "2026-01-02T03:04:05Z"
            };

            var target = new SampleSettings();
            SettingsBinder.Populate(values, target);

            Assert.Equal(42, target.Count);
            Assert.Equal(9_000_000_000L, target.BigCount);
            Assert.True(target.Enabled);
            Assert.Equal(3.14, target.Ratio);
            Assert.Equal(SampleEnum.Second, target.Mode);
            Assert.Equal(values["Identifier"], target.Identifier.ToString());
            Assert.Equal(TimeSpan.FromMinutes(5), target.Interval);
        }

        [Fact]
        public void Populate_EnumAsNumericValue_Converts()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Mode"] = 2 };
            var target = new SampleSettings();
            SettingsBinder.Populate(values, target);
            Assert.Equal(SampleEnum.Second, target.Mode);
        }

        [Fact]
        public void Populate_JsonElementString_ConvertsToEnum()
        {
            using var doc = JsonDocument.Parse("\"Second\"");
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Mode"] = doc.RootElement.Clone() };
            var target = new SampleSettings();
            SettingsBinder.Populate(values, target);
            Assert.Equal(SampleEnum.Second, target.Mode);
        }

        [Fact]
        public void Populate_JsonElementNumber_ConvertsToInt()
        {
            using var doc = JsonDocument.Parse("123");
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Count"] = doc.RootElement.Clone() };
            var target = new SampleSettings();
            SettingsBinder.Populate(values, target);
            Assert.Equal(123, target.Count);
        }

        [Fact]
        public void Populate_JsonElementBoolean_ConvertsToBool()
        {
            using var doc = JsonDocument.Parse("false");
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Enabled"] = doc.RootElement.Clone() };
            var target = new SampleSettings { Enabled = true };
            SettingsBinder.Populate(values, target);
            Assert.False(target.Enabled);
        }

        [Fact]
        public void Populate_JsonElementMalformedGuidString_FallsBackToGuidEmpty()
        {
            // Guid is special-cased inside ConvertJsonElement with Guid.TryParse, unlike
            // every other JSON-string conversion path -- this is the one path that is
            // already defensive against malformed input.
            using var doc = JsonDocument.Parse("\"not-a-guid\"");
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Identifier"] = doc.RootElement.Clone() };
            var target = new SampleSettings { Identifier = Guid.NewGuid() };
            SettingsBinder.Populate(values, target);
            Assert.Equal(Guid.Empty, target.Identifier);
        }

        [Fact]
        public void Populate_NullValue_ForValueType_ResetsToDefault()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Count"] = null };
            var target = new SampleSettings { Count = 99 };
            SettingsBinder.Populate(values, target);
            Assert.Equal(0, target.Count);
        }

        [Fact]
        public void Populate_NullValue_ForNullableValueType_PreservesNull()
        {
            // A Nullable<T> settings field can be cleared: sending null yields null. Previously
            // ConvertValue unwrapped Nullable<T> to T before the null check, coercing null to
            // default(T) = 0 — silently corrupting the value and making the field unclearable.
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["NullableCount"] = null };
            var target = new SampleSettings { NullableCount = 5 };
            SettingsBinder.Populate(values, target);

            Assert.Null(target.NullableCount);
        }

        [Fact]
        public void Populate_CaseInsensitiveKeyLookup_StillConverts()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["count"] = "17" };
            var target = new SampleSettings();
            SettingsBinder.Populate(values, target);
            Assert.Equal(17, target.Count);
        }

        [Fact]
        public void Populate_UnknownKey_IsIgnored()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["DoesNotExist"] = "whatever" };
            var target = new SampleSettings();
            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
        }

        [Fact]
        public void Populate_AlreadyAssignableValue_PassesThroughWithoutConversion()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Count"] = 55 }; // already an int
            var target = new SampleSettings();
            SettingsBinder.Populate(values, target);
            Assert.Equal(55, target.Count);
        }

        // ---------------------------------------------------------------
        // DEFECT CHARACTERIZATION: malformed input is NOT guarded.
        //
        // ConvertValue's fallback path (`Convert.ChangeType(value, destinationType,
        // CultureInfo.InvariantCulture)`) and the direct Guid.Parse / TimeSpan.Parse /
        // DateTime.Parse calls for non-JsonElement string values have NO try/catch.
        // StreamingPlugin.StreamingSettingsProvider.Validate/Apply call
        // SettingsBinder.Populate directly with no surrounding try/catch either
        // (src/Hosting/StreamingPlugin.cs lines ~230-254). So a malformed settings value
        // (e.g. a non-numeric string typed into an int field via the host UI/API) throws
        // an unhandled FormatException/OverflowException/ArgumentException that propagates
        // out of ISettingsProvider.Validate/Apply, instead of producing a
        // PluginValidationResult validation failure. This aborts the settings-save with an
        // unhandled exception rather than a clean validation error.
        //
        // These tests document the CURRENT behavior (not a fix).
        // ---------------------------------------------------------------

        [Fact]
        public void Populate_NonNumericStringForIntField_SkipsFieldGracefully()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Count"] = "not-a-number" };
            var target = new SampleSettings { Count = 42 };

            // A malformed value no longer aborts the whole bind: Populate skips just that field
            // (keeping its prior value) instead of throwing FormatException past the boundary.
            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(42, target.Count);
        }

        [Fact]
        public void Populate_OutOfRangeStringForIntField_SkipsFieldGracefully()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Count"] = "99999999999999999999" };
            var target = new SampleSettings { Count = 7 };

            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(7, target.Count);
        }

        [Fact]
        public void Populate_InvalidEnumString_SkipsFieldGracefully()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Mode"] = "NotARealEnumValue" };
            var target = new SampleSettings { Mode = SampleEnum.Second };

            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(SampleEnum.Second, target.Mode);
        }

        [Fact]
        public void Populate_MalformedGuidString_NonJson_SkipsFieldGracefully()
        {
            // The plain-string Guid path calls Guid.Parse (throws), unlike the JsonElement path
            // (Guid.TryParse → Guid.Empty). The throw is now caught at the Populate loop, so the
            // field keeps its prior value rather than aborting the whole bind.
            var sentinel = Guid.NewGuid();
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Identifier"] = "not-a-guid" };
            var target = new SampleSettings { Identifier = sentinel };

            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(sentinel, target.Identifier);
        }

        [Fact]
        public void Populate_MalformedTimeSpanString_SkipsFieldGracefully()
        {
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Interval"] = "not-a-timespan" };
            var target = new SampleSettings { Interval = TimeSpan.FromMinutes(9) };

            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(TimeSpan.FromMinutes(9), target.Interval);
        }

        [Fact]
        public void Populate_MalformedDateTimeString_SkipsFieldGracefully()
        {
            var sentinel = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Timestamp"] = "not-a-date" };
            var target = new SampleSettings { Timestamp = sentinel };

            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(sentinel, target.Timestamp);
        }

        [Fact]
        public void Populate_JsonElementMalformedDateTimeString_SkipsFieldGracefully()
        {
            // The DateTime JSON path calls DateTime.Parse (throws), unlike the defensive Guid
            // JSON path. The throw is now caught at the Populate loop, so the field keeps its
            // prior value instead of aborting the whole bind.
            var sentinel = new DateTime(2019, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            using var doc = JsonDocument.Parse("\"not-a-date\"");
            IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?> { ["Timestamp"] = doc.RootElement.Clone() };
            var target = new SampleSettings { Timestamp = sentinel };

            var exception = Record.Exception(() => SettingsBinder.Populate(values, target));
            Assert.Null(exception);
            Assert.Equal(sentinel, target.Timestamp);
        }
    }
}
