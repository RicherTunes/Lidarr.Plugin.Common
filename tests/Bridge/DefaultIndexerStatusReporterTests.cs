using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="DefaultIndexerStatusReporter"/> — concrete implementation details
/// such as LastError tracking that are not part of the IIndexerStatusReporter interface.
/// Interface-level lifecycle tests live in CoreCapabilityComplianceTests and BridgeComplianceTests.
/// </summary>
public class DefaultIndexerStatusReporterTests : IDisposable
{
    private readonly BridgeComplianceFixture _fixture = new();

    private DefaultIndexerStatusReporter Reporter =>
        (DefaultIndexerStatusReporter)_fixture.StatusReporter;

    [Fact]
    public async Task Error_Records_LastError()
    {
        var exception = new InvalidOperationException("test error");
        await Reporter.ReportErrorAsync(exception);

        Assert.Same(exception, Reporter.LastError);
    }

    [Fact]
    public async Task NonError_Status_Clears_LastError()
    {
        await Reporter.ReportErrorAsync(new Exception("err"));
        await Reporter.ReportStatusAsync(IndexerStatus.Idle);

        Assert.Null(Reporter.LastError);
    }

    [Fact]
    public async Task Full_Cycle_Records_Then_Clears_LastError()
    {
        // Error sets LastError
        var exception = new InvalidOperationException("test error");
        await Reporter.ReportErrorAsync(exception);
        Assert.Same(exception, Reporter.LastError);

        // Non-error status clears LastError
        await Reporter.ReportStatusAsync(IndexerStatus.Idle);
        Assert.Null(Reporter.LastError);
    }

    public void Dispose() => _fixture.Dispose();
}
