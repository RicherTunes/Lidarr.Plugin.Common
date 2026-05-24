using System.Collections.Generic;
using FluentValidation.Results;
using Lidarr.Plugin.Common.Validation;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Validation
{
    [Trait("Category", "Unit")]
    public class TestValidationBuilderTests
    {
        #region RequireNonEmpty — adds a failure for null/empty/whitespace

        [Fact]
        public void RequireNonEmpty_WithNullValue_AddsFailure()
        {
            var builder = new TestValidationBuilder();
            builder.RequireNonEmpty("MyField", null);

            Assert.True(builder.HasFailures);
            var failures = builder.Build();
            Assert.Single(failures);
            Assert.Equal("MyField", failures[0].PropertyName);
        }

        [Fact]
        public void RequireNonEmpty_WithEmptyString_AddsFailure()
        {
            var builder = new TestValidationBuilder();
            builder.RequireNonEmpty("MyField", "");

            Assert.True(builder.HasFailures);
            Assert.Single(builder.Build());
        }

        [Fact]
        public void RequireNonEmpty_WithWhitespaceOnly_AddsFailure()
        {
            var builder = new TestValidationBuilder();
            builder.RequireNonEmpty("MyField", "   ");

            Assert.True(builder.HasFailures);
            Assert.Single(builder.Build());
        }

        [Fact]
        public void RequireNonEmpty_WithNonEmptyValue_DoesNotAddFailure()
        {
            var builder = new TestValidationBuilder();
            builder.RequireNonEmpty("MyField", "/config/Tidalarr");

            Assert.False(builder.HasFailures);
            Assert.Empty(builder.Build());
        }

        #endregion

        #region Default and custom messages

        [Fact]
        public void RequireNonEmpty_WithoutCustomMessage_UsesDefaultMessage()
        {
            var builder = new TestValidationBuilder();
            builder.RequireNonEmpty("ConfigPath", null);

            var failures = builder.Build();
            Assert.Equal("ConfigPath is required.", failures[0].ErrorMessage);
        }

        [Fact]
        public void RequireNonEmpty_WithCustomMessage_UsesCustomMessage()
        {
            const string CustomMessage = "Config path is required. Default is /config/Tidalarr.";
            var builder = new TestValidationBuilder();
            builder.RequireNonEmpty("ConfigPath", null, CustomMessage);

            var failures = builder.Build();
            Assert.Equal(CustomMessage, failures[0].ErrorMessage);
        }

        #endregion

        #region Accumulate — all failures collected (not just first)
        // This is the fix for PR #130 finding #12 (early-return after first missing field).

        [Fact]
        public void RequireNonEmpty_MultipleFailures_AccumulatesAll()
        {
            var builder = new TestValidationBuilder()
                .RequireNonEmpty("ConfigPath", null, "Config path is required.")
                .RequireNonEmpty("DownloadPath", "", "Download path is required.")
                .RequireNonEmpty("Token", "   ", "Token is required.");

            Assert.True(builder.HasFailures);
            var failures = builder.Build();
            Assert.Equal(3, failures.Count);
            Assert.Equal("ConfigPath", failures[0].PropertyName);
            Assert.Equal("DownloadPath", failures[1].PropertyName);
            Assert.Equal("Token", failures[2].PropertyName);
        }

        [Fact]
        public void RequireNonEmpty_MixedValidAndInvalid_AccumulatesOnlyFailures()
        {
            // Verifies that valid fields don't produce a failure while invalid ones do
            var builder = new TestValidationBuilder()
                .RequireNonEmpty("ValidField", "present")
                .RequireNonEmpty("MissingField", null)
                .RequireNonEmpty("AnotherValidField", "also present")
                .RequireNonEmpty("AnotherMissingField", "");

            var failures = builder.Build();
            Assert.Equal(2, failures.Count);
            Assert.Equal("MissingField", failures[0].PropertyName);
            Assert.Equal("AnotherMissingField", failures[1].PropertyName);
        }

        #endregion

        #region HasFailures reflects state correctly

        [Fact]
        public void HasFailures_BeforeAnyCall_IsFalse()
        {
            var builder = new TestValidationBuilder();
            Assert.False(builder.HasFailures);
        }

        [Fact]
        public void HasFailures_AfterValidField_RemainsFlase()
        {
            var builder = new TestValidationBuilder()
                .RequireNonEmpty("GoodField", "value");

            Assert.False(builder.HasFailures);
        }

        [Fact]
        public void HasFailures_AfterInvalidField_BecomesTrue()
        {
            var builder = new TestValidationBuilder()
                .RequireNonEmpty("BadField", null);

            Assert.True(builder.HasFailures);
        }

        [Fact]
        public void HasFailures_AfterAddFailure_BecomesTrue()
        {
            var builder = new TestValidationBuilder()
                .AddFailure(new ValidationFailure("Test", "Something went wrong"));

            Assert.True(builder.HasFailures);
        }

        #endregion

        #region Build returns correct list

        [Fact]
        public void Build_WithNoFailures_ReturnsEmptyList()
        {
            var result = new TestValidationBuilder()
                .RequireNonEmpty("Field", "ok")
                .Build();

            Assert.Empty(result);
        }

        [Fact]
        public void Build_WithFailures_ReturnsCorrectList()
        {
            var result = new TestValidationBuilder()
                .RequireNonEmpty("FieldA", null, "A is required")
                .RequireNonEmpty("FieldB", "", "B is required")
                .Build();

            Assert.Equal(2, result.Count);
            Assert.Equal("A is required", result[0].ErrorMessage);
            Assert.Equal("B is required", result[1].ErrorMessage);
        }

        [Fact]
        public void Build_CalledMultipleTimes_ReturnsSameSnapshot()
        {
            var builder = new TestValidationBuilder()
                .RequireNonEmpty("X", null);

            var first = builder.Build();
            var second = builder.Build();

            Assert.Equal(first.Count, second.Count);
        }

        #endregion

        #region AddFailure

        [Fact]
        public void AddFailure_AddsProvidedFailure()
        {
            var failure = new ValidationFailure("Test", "Connection failed (HttpRequestException): timeout.");
            var result = new TestValidationBuilder()
                .AddFailure(failure)
                .Build();

            Assert.Single(result);
            Assert.Same(failure, result[0]);
        }

        [Fact]
        public void AddFailure_CanBeChainedWithRequireNonEmpty()
        {
            var builder = new TestValidationBuilder()
                .RequireNonEmpty("ConfigPath", null)
                .AddFailure(new ValidationFailure("Auth", "No tokens found"));

            Assert.Equal(2, builder.Build().Count);
        }

        #endregion

        #region AddRange integration pattern (canonical call-site pattern)

        [Fact]
        public void CanonicalPattern_AddRangeThenCheck_CollectsAllAndStopsBeforeSmoke()
        {
            // This mirrors the exact call-site pattern every plugin should use:
            //   failures.AddRange(builder.Build());
            //   if (builder.HasFailures) return;
            var outerFailures = new List<ValidationFailure>();

            var builder = new TestValidationBuilder()
                .RequireNonEmpty("ConfigPath", null, "Config path is required.")
                .RequireNonEmpty("DownloadPath", "", "Download path is required.");

            outerFailures.AddRange(builder.Build());

            // Both failures collected — smoke HTTP call correctly skipped
            Assert.Equal(2, outerFailures.Count);
            Assert.True(builder.HasFailures);
        }

        #endregion
    }
}
