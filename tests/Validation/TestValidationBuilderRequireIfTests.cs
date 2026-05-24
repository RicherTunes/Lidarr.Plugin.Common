using System.Collections.Generic;
using FluentValidation.Results;
using Lidarr.Plugin.Common.Validation;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Validation;

/// <summary>
/// Tests for the <see cref="TestValidationBuilder.RequireIf"/> and
/// <see cref="TestValidationBuilder.ApplyTo"/> extensions added in May 2026.
/// The base <see cref="TestValidationBuilder.RequireNonEmpty"/> behavior is covered by the
/// pre-existing test file (May 2026 hardening pass).
/// </summary>
public class TestValidationBuilderRequireIfTests
{
    [Fact]
    public void RequireIf_ConditionFalse_NoFailureEvenWhenValueIsBlank()
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: false, "X", null, "X required when Y is true.");
        Assert.False(b.HasFailures);
    }

    [Fact]
    public void RequireIf_ConditionTrueAndValueBlank_AddsFailure()
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: true, "X", "", "X required when Y is true.");
        Assert.True(b.HasFailures);
        Assert.Equal("X", b.Build()[0].PropertyName);
    }

    [Fact]
    public void RequireIf_ConditionTrueAndValuePresent_NoFailure()
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: true, "X", "hello", "X required when Y is true.");
        Assert.False(b.HasFailures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RequireIf_BlankVariants_AllAddFailureWhenConditionTrue(string? value)
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: true, "Field", value, "msg");
        Assert.True(b.HasFailures);
    }

    [Fact]
    public void RequireIf_NoMessageDefaultsToFieldNameMessage()
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: true, "MyField", "", null);
        Assert.Equal("MyField is required.", b.Build()[0].ErrorMessage);
    }

    [Fact]
    public void ApplyTo_AppendsToExistingFailuresList()
    {
        // The plugin's Test() override receives a List<ValidationFailure> from the host;
        // ApplyTo appends rather than replacing — supports the host pre-populating failures.
        var hostFailures = new List<ValidationFailure>
        {
            new("HostField", "host already added this"),
        };

        new TestValidationBuilder()
            .RequireNonEmpty("MyField", "", "my msg")
            .ApplyTo(hostFailures);

        Assert.Equal(2, hostFailures.Count);
        Assert.Equal("HostField", hostFailures[0].PropertyName);
        Assert.Equal("MyField", hostFailures[1].PropertyName);
    }

    [Fact]
    public void ApplyTo_NullList_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            new TestValidationBuilder()
                .RequireNonEmpty("MyField", "", "my msg")
                .ApplyTo(null!));
        Assert.Null(ex);
    }

    [Fact]
    public void Chained_Mix_OfRequireNonEmptyAndRequireIf_AccumulatesAll()
    {
        var b = new TestValidationBuilder()
            .RequireNonEmpty("A", null, "A required")
            .RequireIf(condition: false, "B", null, "B required when something")
            .RequireNonEmpty("C", "value", "C required")
            .RequireIf(condition: true, "D", null, "D required when something");

        Assert.True(b.HasFailures);
        var failures = b.Build();
        Assert.Equal(2, failures.Count);
        Assert.Equal("A", failures[0].PropertyName);
        Assert.Equal("D", failures[1].PropertyName);
    }
}
