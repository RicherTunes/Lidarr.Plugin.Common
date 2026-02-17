// <copyright file="TriageReasonCodeTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Linq;
using System.Reflection;
using Lidarr.Plugin.Common.Abstractions.Triage;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Triage;

public class TriageReasonCodeTests
{
    [Fact]
    public void AllReasonCodes_AreUpperSnakeCase()
    {
        var fields = typeof(TriageReasonCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));

        foreach (var field in fields)
        {
            var value = (string)field.GetValue(null)!;
            Assert.Matches("^[A-Z][A-Z0-9_]+$", value);
        }
    }

    [Fact]
    public void AllReasonCodes_AreUnique()
    {
        var fields = typeof(TriageReasonCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        Assert.Equal(fields.Count, fields.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AllReasonCodes_AreNonEmpty()
    {
        var fields = typeof(TriageReasonCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));

        foreach (var field in fields)
        {
            var value = (string)field.GetValue(null)!;
            Assert.False(string.IsNullOrWhiteSpace(value), $"{field.Name} must not be empty");
        }
    }

    [Fact]
    public void ConfidenceBand_HasExpectedValues()
    {
        var values = Enum.GetValues<ConfidenceBand>();
        Assert.Contains(ConfidenceBand.High, values);
        Assert.Contains(ConfidenceBand.Medium, values);
        Assert.Contains(ConfidenceBand.Low, values);
        Assert.Equal(3, values.Length);
    }

    [Theory]
    [InlineData(nameof(TriageReasonCodes.ConfidenceBelowThreshold), "CONFIDENCE_BELOW_THRESHOLD")]
    [InlineData(nameof(TriageReasonCodes.ConfidenceFarBelowThreshold), "CONFIDENCE_FAR_BELOW_THRESHOLD")]
    [InlineData(nameof(TriageReasonCodes.MissingRequiredMbids), "MISSING_REQUIRED_MBIDS")]
    [InlineData(nameof(TriageReasonCodes.DuplicateSignal), "DUPLICATE_SIGNAL")]
    [InlineData(nameof(TriageReasonCodes.HighConfidenceWithMbid), "HIGH_CONFIDENCE_WITH_MBID")]
    [InlineData(nameof(TriageReasonCodes.ConsistentSignals), "CONSISTENT_SIGNALS")]
    [InlineData(nameof(TriageReasonCodes.CalibrationApplied), "CALIBRATION_APPLIED")]
    [InlineData(nameof(TriageReasonCodes.LowCalibrationProvider), "LOW_CALIBRATION_PROVIDER")]
    public void ReasonCode_HasExpectedValue(string fieldName, string expectedValue)
    {
        var field = typeof(TriageReasonCodes).GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(expectedValue, (string)field!.GetValue(null)!);
    }
}
