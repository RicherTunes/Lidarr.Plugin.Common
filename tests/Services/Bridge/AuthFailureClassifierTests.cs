using System;
using System.Net.Http;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Bridge;

[Trait("Category", "Unit")]
public class AuthFailureClassifierTests
{
    // The canonical search executor's all-failed signal — MUST NOT be classified as auth
    // (it is an InvalidOperationException, the same type tidal/amazon throw for "not authenticated").
    private const string ExecutorAllFailedMessage =
        "All 3 Tidal request(s) failed; surfacing the error instead of an empty result.";

    [Fact]
    public void AuthGatedException_isAuthFailure()
    {
        var ex = new AuthGatedException(AuthStatus.Failed, "auth latched", "401");
        Assert.True(AuthFailureClassifier.IsAuthFailure(ex));
        Assert.NotNull(AuthFailureClassifier.Classify(ex));
    }

    [Theory]
    [InlineData("Not authenticated")]                                              // tidal
    [InlineData("Amazon Music is not authenticated. Run scripts/amazon-login.py")] // amazon
    [InlineData("A valid Apple Music user token is required for this operation.")]  // apple
    [InlineData("Authentication failed. Verify your Email + Password")]             // qobuz
    [InlineData("Credential validation failed")]                                    // qobuz
    [InlineData("Invalid user ID or auth token. The token may have expired")]       // qobuz
    public void RealPluginAuthMessages_areAuthFailures(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(AuthFailureClassifier.IsAuthFailure(ex), $"should classify as auth: '{message}'");
    }

    [Fact]
    public void ExecutorAllFailedSignal_isNotAuthFailure()
    {
        // THE critical contract: the all-failed IOE shares the type with auth IOEs but must propagate.
        var ex = new InvalidOperationException(ExecutorAllFailedMessage);
        Assert.False(AuthFailureClassifier.IsAuthFailure(ex));
        Assert.Null(AuthFailureClassifier.Classify(ex));
    }

    [Fact]
    public void GenericFailures_areNotAuthFailures()
    {
        Assert.False(AuthFailureClassifier.IsAuthFailure(new InvalidOperationException("boom")));
        Assert.False(AuthFailureClassifier.IsAuthFailure(new HttpRequestException("Internal Server Error")));
        Assert.False(AuthFailureClassifier.IsAuthFailure(new TimeoutException()));
        Assert.False(AuthFailureClassifier.IsAuthFailure(null));
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(200, false)]
    public void StatusCodeExtractor_recognizes401And403Only(int status, bool expected)
    {
        // amazon's HTTP client throws a bare HttpRequestException with NO status; a statusOf delegate
        // lets the classifier still see 401/403.
        var ex = new HttpRequestException("request failed");
        Assert.Equal(expected, AuthFailureClassifier.IsAuthFailure(ex, _ => status));
    }

    [Fact]
    public void Classify_carriesStatusCodeAndMessage()
    {
        var ex = new HttpRequestException("forbidden");
        var failure = AuthFailureClassifier.Classify(ex, _ => 403);
        Assert.NotNull(failure);
        Assert.Equal("403", failure!.ErrorCode);
        Assert.Equal("forbidden", failure.Message);
    }
}
