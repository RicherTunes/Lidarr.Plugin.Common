using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Abstractions.Results;
using Lidarr.Plugin.Common.Extensions;
using Lidarr.Plugin.Common.Services.Bridge;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance
{
    /// <summary>
    /// Layer 1: Core Capability Suite
    /// Tests core plugin infrastructure contracts using fixture-backed implementations.
    /// Bridge services are tested via real DI activation and real default implementations
    /// rather than mock scaffolding.
    /// </summary>
    public class CoreCapabilityComplianceTests : IDisposable
    {
        private readonly BridgeComplianceFixture _fixture = new();

        #region Bridge DI Activation Tests

        [Fact]
        public void Bridge_Services_Resolve_Through_DI_Container()
        {
            // All three bridge services must be resolvable from the fixture's DI container
            Assert.NotNull(_fixture.AuthHandler);
            Assert.NotNull(_fixture.StatusReporter);
            Assert.NotNull(_fixture.RateLimitReporter);
        }

        [Fact]
        public void Bridge_Services_Are_Singleton_Instances()
        {
            // Services resolved multiple times from the same container must return the same instance
            var auth1 = _fixture.Services.GetRequiredService<IAuthFailureHandler>();
            var auth2 = _fixture.Services.GetRequiredService<IAuthFailureHandler>();
            Assert.Same(auth1, auth2);

            var status1 = _fixture.Services.GetRequiredService<IIndexerStatusReporter>();
            var status2 = _fixture.Services.GetRequiredService<IIndexerStatusReporter>();
            Assert.Same(status1, status2);

            var rate1 = _fixture.Services.GetRequiredService<IRateLimitReporter>();
            var rate2 = _fixture.Services.GetRequiredService<IRateLimitReporter>();
            Assert.Same(rate1, rate2);
        }

        [Fact]
        public void Bridge_Defaults_Yield_Correct_Implementation_Types()
        {
            Assert.IsType<DefaultAuthFailureHandler>(_fixture.AuthHandler);
            Assert.IsType<DefaultIndexerStatusReporter>(_fixture.StatusReporter);
            Assert.IsType<DefaultRateLimitReporter>(_fixture.RateLimitReporter);
        }

        [Fact]
        public void Custom_Registration_Takes_Precedence_Over_Defaults()
        {
            // Arrange: register a custom handler BEFORE calling AddBridgeDefaults
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(_fixture.Context.LoggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            var customHandler = new TestAuthHandler();
            services.AddSingleton<IAuthFailureHandler>(customHandler);
            services.AddBridgeDefaults();

            using var provider = services.BuildServiceProvider();

            // Assert: custom registration wins (TryAdd semantics)
            Assert.Same(customHandler, provider.GetRequiredService<IAuthFailureHandler>());
            // Other defaults still resolve
            Assert.IsType<DefaultIndexerStatusReporter>(provider.GetRequiredService<IIndexerStatusReporter>());
            Assert.IsType<DefaultRateLimitReporter>(provider.GetRequiredService<IRateLimitReporter>());
        }

        #endregion

        #region Auth Failure Handler Behavioral Tests

        [Fact]
        public async Task AuthHandler_Full_Lifecycle_Unknown_To_Failed_To_Authenticated()
        {
            // Initial state
            Assert.Equal(AuthStatus.Unknown, _fixture.AuthHandler.Status);

            // Failure transition
            await _fixture.AuthHandler.HandleFailureAsync(
                new AuthFailure { ErrorCode = "AUTH001", Message = "Token expired" });
            Assert.Equal(AuthStatus.Failed, _fixture.AuthHandler.Status);

            // Recovery transition
            await _fixture.AuthHandler.HandleSuccessAsync();
            Assert.Equal(AuthStatus.Authenticated, _fixture.AuthHandler.Status);
        }

        [Fact]
        public async Task AuthHandler_Reauth_Request_Transitions_Through_Expired()
        {
            // Start authenticated
            await _fixture.AuthHandler.HandleSuccessAsync();
            Assert.Equal(AuthStatus.Authenticated, _fixture.AuthHandler.Status);

            // Request reauth
            await _fixture.AuthHandler.RequestReauthenticationAsync("Token revoked by user");
            Assert.Equal(AuthStatus.Expired, _fixture.AuthHandler.Status);

            // Recover
            await _fixture.AuthHandler.HandleSuccessAsync();
            Assert.Equal(AuthStatus.Authenticated, _fixture.AuthHandler.Status);
        }

        [Fact]
        public async Task AuthHandler_Failure_Records_And_Success_Clears_LastFailure()
        {
            var handler = (DefaultAuthFailureHandler)_fixture.AuthHandler;

            // Failure records details
            var failure = new AuthFailure { ErrorCode = "E001", Message = "test failure" };
            await handler.HandleFailureAsync(failure);
            Assert.NotNull(handler.LastFailure);
            Assert.Equal("E001", handler.LastFailure!.ErrorCode);

            // Success clears failure details
            await handler.HandleSuccessAsync();
            Assert.Null(handler.LastFailure);
        }

        [Fact]
        public async Task AuthHandler_Failure_Logs_Warning_With_Error_Code()
        {
            await _fixture.AuthHandler.HandleFailureAsync(
                new AuthFailure { ErrorCode = "AUTH_EXPIRED", Message = "Session timed out" });

            var logs = _fixture.Context.LogEntries.Snapshot();
            Assert.Contains(logs, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("AUTH_EXPIRED"));
        }

        [Fact]
        public async Task AuthHandler_Null_Failure_Throws_ArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _fixture.AuthHandler.HandleFailureAsync(null!));
        }

        #endregion

        #region Indexer Status Reporter Behavioral Tests

        [Fact]
        public async Task StatusReporter_Full_Lifecycle_Idle_To_Searching_To_Error_To_Idle()
        {
            // Initial state
            Assert.Equal(IndexerStatus.Idle, _fixture.StatusReporter.CurrentStatus);

            // Start searching
            await _fixture.StatusReporter.ReportStatusAsync(IndexerStatus.Searching);
            Assert.Equal(IndexerStatus.Searching, _fixture.StatusReporter.CurrentStatus);

            // Error during search
            await _fixture.StatusReporter.ReportErrorAsync(new InvalidOperationException("API down"));
            Assert.Equal(IndexerStatus.Error, _fixture.StatusReporter.CurrentStatus);

            // Recover to idle
            await _fixture.StatusReporter.ReportStatusAsync(IndexerStatus.Idle);
            Assert.Equal(IndexerStatus.Idle, _fixture.StatusReporter.CurrentStatus);
        }

        [Fact]
        public async Task StatusReporter_Error_Records_And_NonError_Clears_LastError()
        {
            var reporter = (DefaultIndexerStatusReporter)_fixture.StatusReporter;

            // Error sets LastError
            var exception = new InvalidOperationException("test error");
            await reporter.ReportErrorAsync(exception);
            Assert.Same(exception, reporter.LastError);

            // Non-error status clears LastError
            await reporter.ReportStatusAsync(IndexerStatus.Idle);
            Assert.Null(reporter.LastError);
        }

        [Fact]
        public async Task StatusReporter_Error_Produces_Error_Level_Log()
        {
            await _fixture.StatusReporter.ReportErrorAsync(new InvalidOperationException("test error"));

            var logs = _fixture.Context.LogEntries.Snapshot();
            Assert.Contains(logs, e => e.Level == LogLevel.Error);
        }

        [Fact]
        public async Task StatusReporter_Null_Error_Throws_ArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await _fixture.StatusReporter.ReportErrorAsync(null!));
        }

        #endregion

        #region Rate Limit Reporter Behavioral Tests

        [Fact]
        public async Task RateLimitReporter_Full_Lifecycle_Clear_To_Limited_To_Cleared()
        {
            // Initial state
            Assert.False(_fixture.RateLimitReporter.Status.IsRateLimited);

            // Hit rate limit
            await _fixture.RateLimitReporter.ReportRateLimitAsync(TimeSpan.FromSeconds(30));
            Assert.True(_fixture.RateLimitReporter.Status.IsRateLimited);
            Assert.NotNull(_fixture.RateLimitReporter.Status.ResetAt);

            // Rate limit cleared
            await _fixture.RateLimitReporter.ReportRateLimitClearedAsync();
            Assert.False(_fixture.RateLimitReporter.Status.IsRateLimited);
        }

        [Fact]
        public async Task RateLimitReporter_ResetAt_Is_In_The_Future()
        {
            var before = DateTimeOffset.UtcNow;
            await _fixture.RateLimitReporter.ReportRateLimitAsync(TimeSpan.FromSeconds(60));

            Assert.True(_fixture.RateLimitReporter.Status.ResetAt >= before.AddSeconds(59));
        }

        [Fact]
        public async Task RateLimitReporter_Backoff_Logs_Warning_With_Reason()
        {
            await _fixture.RateLimitReporter.ReportBackoffAsync(
                TimeSpan.FromSeconds(5), "exponential");

            var logs = _fixture.Context.LogEntries.Snapshot();
            Assert.Contains(logs, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("exponential"));
        }

        #endregion

        #region Error Contract Tests

        [Fact]
        public void PluginError_Contains_Stable_Error_Code()
        {
            // Arrange & Act
            var error = new PluginError(PluginErrorCode.RateLimited, "Rate limited");

            // Assert
            Assert.Equal(PluginErrorCode.RateLimited, error.Code);
            Assert.NotNull(error.Message);
        }

        [Fact]
        public void PluginOperationResult_Failure_Contains_PluginError()
        {
            // Arrange
            var error = new PluginError(PluginErrorCode.AuthenticationExpired, "Auth expired");

            // Act
            var result = PluginOperationResult<StreamingAlbum>.Failure(error);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.NotNull(result.Error);
            Assert.Equal(PluginErrorCode.AuthenticationExpired, result.Error.Code);
        }

        [Fact]
        public void PluginOperationResult_Success_Contains_Value()
        {
            // Arrange & Act
            var result = PluginOperationResult.Success();

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Null(result.Error);
        }

        [Fact]
        public void PluginOperationResult_Failure_EnsureSuccess_Throws()
        {
            // Arrange
            var error = new PluginError(PluginErrorCode.RateLimited, "Rate limited");
            var result = PluginOperationResult.Failure(error);

            // Act & Assert
            var ex = Assert.Throws<PluginOperationException>(() => result.EnsureSuccess());
            Assert.Equal(PluginErrorCode.RateLimited, ex.Error.Code);
        }

        [Fact]
        public void PluginError_FromException_Wraps_Exception_With_Unknown_Code()
        {
            // Arrange
            var exception = new InvalidOperationException("Something went wrong");

            // Act
            var error = PluginError.FromException(exception);

            // Assert
            Assert.Equal(PluginErrorCode.Unknown, error.Code);
            Assert.Same(exception, error.Exception);
            Assert.Contains("Something went wrong", error.Message);
        }

        [Fact]
        public void PluginError_WithMetadata_Merges_Metadata()
        {
            // Arrange
            var error = new PluginError(PluginErrorCode.RateLimited, "Rate limited");

            // Act
            var enriched = error.WithMetadata(new System.Collections.Generic.Dictionary<string, string>
            {
                ["retry-after"] = "30",
                ["provider"] = "test"
            });

            // Assert
            Assert.Equal("30", enriched.Metadata["retry-after"]);
            Assert.Equal("test", enriched.Metadata["provider"]);
            Assert.Equal(PluginErrorCode.RateLimited, enriched.Code);
        }

        #endregion

        #region Helpers

        public void Dispose() => _fixture.Dispose();

        private sealed class TestAuthHandler : IAuthFailureHandler
        {
            public AuthStatus Status => AuthStatus.Unknown;
            public ValueTask HandleFailureAsync(AuthFailure failure, System.Threading.CancellationToken ct = default) => default;
            public ValueTask HandleSuccessAsync(System.Threading.CancellationToken ct = default) => default;
            public ValueTask RequestReauthenticationAsync(string reason, System.Threading.CancellationToken ct = default) => default;
        }

        #endregion
    }
}
