using System;
using System.Net.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

/// <summary>
/// Wave 87 TDD: TestFailureFormatter consolidates the "Test failed
/// ({ExceptionType}): {message}. Full details in Lidarr logs." pattern
/// that was duplicated across applemusicarr, qobuzarr, and tidalarr
/// Test() catch arms (waves 73, 74, 75).
/// </summary>
public sealed class TestFailureFormatterTests
{
    [Fact]
    public void Format_ExceptionWithMessage_IncludesTypeAndMessage()
    {
        var ex = new InvalidOperationException("connection refused");

        var msg = TestFailureFormatter.Format(ex);

        // Must include the exception type so users can tell network from auth
        // from quota errors at a glance.
        Assert.Contains("InvalidOperationException", msg);
        Assert.Contains("connection refused", msg);
    }

    [Fact]
    public void Format_DefaultPrefix_StartsWithTestFailed()
    {
        var ex = new HttpRequestException("DNS lookup failed");
        var msg = TestFailureFormatter.Format(ex);
        Assert.StartsWith("Test failed", msg);
    }

    [Fact]
    public void Format_CustomPrefix_UsesIt()
    {
        var ex = new InvalidOperationException("auth expired");
        var msg = TestFailureFormatter.Format(ex, prefix: "Connection test failed");
        Assert.StartsWith("Connection test failed", msg);
    }

    [Fact]
    public void Format_AppendsLogReference()
    {
        // Pin the contract that the formatted message points users at the
        // Lidarr log viewer for the full stack trace.
        var ex = new InvalidOperationException("x");
        var msg = TestFailureFormatter.Format(ex);
        Assert.Contains("Lidarr logs", msg);
    }

    [Fact]
    public void Format_NullException_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TestFailureFormatter.Format(null!));
    }

    [Fact]
    public void Format_HttpRequestException_PreservesNetworkSignal()
    {
        // Common scenario: HttpRequestException is the canonical "network problem"
        // signal — by including the type users can self-triage as a network
        // issue without reading the message.
        var ex = new HttpRequestException("Connection timed out");
        var msg = TestFailureFormatter.Format(ex);
        Assert.Contains("HttpRequestException", msg);
    }
}
