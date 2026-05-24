using System.Linq;
using FluentValidation.Results;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="TestValidationBuilder"/>. The builder solves the
/// "early-return after first missing field" anti-pattern that all 3 plugins independently
/// fell into (PR #130 review #1 finding #12). Centralizing the accumulate-then-return
/// shape means: one ergonomic API in Common, plugins drop ~30 LOC each of chained
/// `if (string.IsNullOrWhiteSpace(...)) { failures.Add(...); return; }` ladders.
/// </summary>
public class TestValidationBuilderTests
{
    [Fact]
    public void RequireNonEmpty_BlankValue_AddsFailure()
    {
        var b = new TestValidationBuilder();
        b.RequireNonEmpty("Username", "", "Username is required.");

        Assert.True(b.HasFailures);
        var failures = b.ToFailures();
        Assert.Single(failures);
        Assert.Equal("Username", failures[0].PropertyName);
        Assert.Equal("Username is required.", failures[0].ErrorMessage);
    }

    [Theory]
    [InlineData("real value")]
    [InlineData("  trimmed-ok  ")]
    public void RequireNonEmpty_PresentValue_NoFailure(string value)
    {
        var b = new TestValidationBuilder();
        b.RequireNonEmpty("Username", value, "Username is required.");
        Assert.False(b.HasFailures);
        Assert.Empty(b.ToFailures());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RequireNonEmpty_BlankVariants_AddFailure(string? value)
    {
        var b = new TestValidationBuilder();
        b.RequireNonEmpty("Field", value, "msg");
        Assert.True(b.HasFailures);
    }

    [Fact]
    public void Builder_AccumulatesAllMissingFields()
    {
        var b = new TestValidationBuilder();
        b.RequireNonEmpty("A", "", "A required");
        b.RequireNonEmpty("B", "", "B required");
        b.RequireNonEmpty("C", "value", "C required");
        b.RequireNonEmpty("D", null, "D required");

        Assert.True(b.HasFailures);
        var failures = b.ToFailures();
        Assert.Equal(3, failures.Count);

        Assert.Equal(new[] { "A", "B", "D" },
            failures.Select(f => f.PropertyName).ToArray());
    }

    [Fact]
    public void RequireIf_ConditionFalse_NoFailureEvenWhenValueIsBlank()
    {
        var b = new TestValidationBuilder();
        // condition=false means "this check doesn't apply" — common pattern is "X required when Y"
        b.RequireIf(condition: false, "X", null, "X required when Y is true.");
        Assert.False(b.HasFailures);
    }

    [Fact]
    public void RequireIf_ConditionTrueAndValueBlank_AddsFailure()
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: true, "X", "", "X required when Y is true.");
        Assert.True(b.HasFailures);
        Assert.Equal("X", b.ToFailures()[0].PropertyName);
    }

    [Fact]
    public void RequireIf_ConditionTrueAndValuePresent_NoFailure()
    {
        var b = new TestValidationBuilder();
        b.RequireIf(condition: true, "X", "hello", "X required when Y is true.");
        Assert.False(b.HasFailures);
    }

    [Fact]
    public void ApplyTo_AppendsToExistingFailuresList()
    {
        // The plugin's Test() override receives a List<ValidationFailure> from the host;
        // the builder appends rather than replacing — supports cases where the host
        // pre-populated failures.
        var hostFailures = new System.Collections.Generic.List<ValidationFailure>
        {
            new("HostField", "host already added this"),
        };

        var b = new TestValidationBuilder();
        b.RequireNonEmpty("MyField", "", "my msg");
        b.ApplyTo(hostFailures);

        Assert.Equal(2, hostFailures.Count);
        Assert.Equal("HostField", hostFailures[0].PropertyName);
        Assert.Equal("MyField", hostFailures[1].PropertyName);
    }
}
