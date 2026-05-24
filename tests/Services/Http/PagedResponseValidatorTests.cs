using System;

using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Services.Http;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http;

/// <summary>
/// Tests for <see cref="PagedResponseValidator"/> — APL-010.
/// </summary>
public sealed class PagedResponseValidatorTests
{
    // ------------------------------------------------------------------ //
    // Happy paths
    // ------------------------------------------------------------------ //

    [Fact]
    public void Validate_ExactMatch_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            PagedResponseValidator.Validate(receivedItemCount: 50, declaredTotalCount: 50, contextName: "albums"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_NullDeclaredTotal_DoesNotThrow()
    {
        // APIs that do not declare a total count must be allowed through.
        var ex = Record.Exception(() =>
            PagedResponseValidator.Validate(receivedItemCount: 25, declaredTotalCount: null, contextName: "tracks"));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_ZeroItems_ZeroDeclaredTotal_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            PagedResponseValidator.Validate(receivedItemCount: 0, declaredTotalCount: 0, contextName: "empty-search"));
        Assert.Null(ex);
    }

    // ------------------------------------------------------------------ //
    // Mismatch — must throw PagedResponseIntegrityException
    // ------------------------------------------------------------------ //

    [Fact]
    public void Validate_ReceivedLessThanDeclared_ThrowsPagedResponseIntegrityException()
    {
        var ex = Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 40, declaredTotalCount: 50, contextName: "albums"));
        Assert.IsType<PagedResponseIntegrityException>(ex);
    }

    [Fact]
    public void Validate_ReceivedMoreThanDeclared_ThrowsPagedResponseIntegrityException()
    {
        // Over-fetching is also a protocol violation (duplicate pages?).
        Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 60, declaredTotalCount: 50, contextName: "albums"));
    }

    [Fact]
    public void Validate_Mismatch_MessageContainsReceivedCount()
    {
        var ex = Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 7, declaredTotalCount: 10, contextName: "playlists"));
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void Validate_Mismatch_MessageContainsDeclaredCount()
    {
        var ex = Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 7, declaredTotalCount: 10, contextName: "playlists"));
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public void Validate_Mismatch_MessageContainsContextName()
    {
        const string ctx = "apple-music-albums";
        var ex = Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 3, declaredTotalCount: 5, contextName: ctx));
        Assert.Contains(ctx, ex.Message);
    }

    [Fact]
    public void Validate_Mismatch_ExceptionExposesReceivedAndDeclaredProperties()
    {
        var ex = Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 12, declaredTotalCount: 20, contextName: "ctx"));
        Assert.Equal(12, ex.ReceivedItemCount);
        Assert.Equal(20, ex.DeclaredTotalCount);
    }

    [Fact]
    public void Validate_Mismatch_ExceptionExposesContextName()
    {
        var ex = Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: 1, declaredTotalCount: 2, contextName: "my-context"));
        Assert.Equal("my-context", ex.ContextName);
    }

    // ------------------------------------------------------------------ //
    // Edge cases
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(99, 100)]
    [InlineData(100, 99)]
    public void Validate_OffByOne_ThrowsPagedResponseIntegrityException(int received, int declared)
    {
        Assert.Throws<PagedResponseIntegrityException>(() =>
            PagedResponseValidator.Validate(receivedItemCount: received, declaredTotalCount: declared, contextName: "edge"));
    }

    [Fact]
    public void Validate_NegativeReceivedCount_WithNullTotal_DoesNotThrow()
    {
        // When no declared total, we cannot validate — allow through.
        var ex = Record.Exception(() =>
            PagedResponseValidator.Validate(receivedItemCount: -1, declaredTotalCount: null, contextName: "edge"));
        Assert.Null(ex);
    }
}
