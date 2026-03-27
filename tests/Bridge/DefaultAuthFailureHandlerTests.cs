using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="DefaultAuthFailureHandler"/> — concrete implementation details
/// such as LastFailure tracking that are not part of the IAuthFailureHandler interface.
/// Interface-level lifecycle tests live in CoreCapabilityComplianceTests and BridgeComplianceTests.
/// </summary>
public class DefaultAuthFailureHandlerTests : IDisposable
{
    private readonly BridgeComplianceFixture _fixture = new();

    private DefaultAuthFailureHandler Handler =>
        (DefaultAuthFailureHandler)_fixture.AuthHandler;

    [Fact]
    public async Task Failure_Records_LastFailure()
    {
        var failure = new AuthFailure { ErrorCode = "E001", Message = "test failure" };
        await Handler.HandleFailureAsync(failure);

        Assert.NotNull(Handler.LastFailure);
        Assert.Equal("E001", Handler.LastFailure!.ErrorCode);
    }

    [Fact]
    public async Task Success_Clears_LastFailure()
    {
        await Handler.HandleFailureAsync(
            new AuthFailure { ErrorCode = "E001", Message = "test failure" });
        await Handler.HandleSuccessAsync();

        Assert.Null(Handler.LastFailure);
    }

    [Fact]
    public async Task Full_Cycle_Records_Then_Clears_LastFailure()
    {
        // Failure records details
        var failure = new AuthFailure { ErrorCode = "E001", Message = "test failure" };
        await Handler.HandleFailureAsync(failure);
        Assert.NotNull(Handler.LastFailure);
        Assert.Equal("E001", Handler.LastFailure!.ErrorCode);

        // Success clears failure details
        await Handler.HandleSuccessAsync();
        Assert.Null(Handler.LastFailure);
    }

    public void Dispose() => _fixture.Dispose();
}
