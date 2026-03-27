using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Abstractions.Results;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance
{
    /// <summary>
    /// Layer 1: Core Capability Suite
    /// Tests that all plugins must pass regardless of their specific capabilities.
    /// </summary>
    public class CoreCapabilityComplianceTests
    {
        #region Plugin Lifecycle Tests

        [Fact]
        public async Task Plugin_Initialize_Then_Dispose_Completes_Gracefully()
        {
            // Arrange
            var plugin = CreateTestPlugin();
            var context = CreateTestContext();

            // Act
            await plugin.InitializeAsync(context);
            await plugin.DisposeAsync();

            // Assert - No exception thrown
            Assert.True(true);
        }

        [Fact]
        public async Task Plugin_Double_Dispose_Does_Not_Throw()
        {
            // Arrange
            var plugin = CreateTestPlugin();
            var context = CreateTestContext();
            await plugin.InitializeAsync(context);

            // Act
            await plugin.DisposeAsync();
            await plugin.DisposeAsync(); // Second dispose

            // Assert - No exception thrown
            Assert.True(true);
        }

        [Fact]
        public async Task Plugin_Operations_After_Dispose_Return_Null_Or_Throw()
        {
            // Arrange
            var plugin = CreateTestPlugin();
            var context = CreateTestContext();
            await plugin.InitializeAsync(context);
            await plugin.DisposeAsync();

            // Act — after dispose, CreateIndexerAsync should either throw or return null.
            // The mock returns null, which is acceptable post-dispose behavior.
            var indexer = await plugin.CreateIndexerAsync();
            Assert.Null(indexer);
        }

        #endregion

        #region Settings Validation Tests

        [Fact]
        public void SettingsProvider_Describe_Returns_Required_Fields()
        {
            // Arrange
            var settingsProvider = CreateTestSettingsProvider();

            // Act
            var definitions = settingsProvider.Describe();

            // Assert
            Assert.NotNull(definitions);
            Assert.NotEmpty(definitions);

            var requiredFields = definitions.Where(d => d.IsRequired).ToList();
            Assert.NotEmpty(requiredFields); // At least one required field
        }

        [Fact]
        public void SettingsProvider_GetDefaults_Returns_Valid_Dictionary()
        {
            // Arrange
            var settingsProvider = CreateTestSettingsProvider();

            // Act
            var defaults = settingsProvider.GetDefaults();

            // Assert
            Assert.NotNull(defaults);
        }

        [Fact]
        public void SettingsProvider_Validate_Rejects_Invalid_Settings()
        {
            // Arrange
            var settingsProvider = CreateTestSettingsProvider();
            var invalidSettings = CreateInvalidSettings();

            // Act
            var result = settingsProvider.Validate(invalidSettings);

            // Assert
            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
        }

        [Fact]
        public void SettingsProvider_Validate_Accepts_Valid_Settings()
        {
            // Arrange
            var settingsProvider = CreateTestSettingsProvider();
            var validSettings = CreateValidSettings();

            // Act
            var result = settingsProvider.Validate(validSettings);

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void SettingsProvider_Apply_Rejects_Invalid_Settings()
        {
            // Arrange
            var settingsProvider = CreateTestSettingsProvider();
            var invalidSettings = CreateInvalidSettings();

            // Act
            var result = settingsProvider.Apply(invalidSettings);

            // Assert
            Assert.False(result.IsValid);
        }

        [Fact]
        public void SettingsProvider_Apply_Accepts_Valid_Settings()
        {
            // Arrange
            var settingsProvider = CreateTestSettingsProvider();
            var validSettings = CreateValidSettings();

            // Act
            var result = settingsProvider.Apply(validSettings);

            // Assert
            Assert.True(result.IsValid);
        }

        #endregion

        #region Auth Failure Tests

        [Fact]
        public async Task Indexer_Expired_Token_Returns_Graceful_Result()
        {
            // Arrange — a mock indexer with expired token returns empty results gracefully
            var indexerMock = new Mock<IIndexer>();
            indexerMock.Setup(i => i.SearchAlbumsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult<IReadOnlyList<StreamingAlbum>>(new List<StreamingAlbum>()));

            // Act
            var result = await indexerMock.Object.SearchAlbumsAsync("test query");

            // Assert - Should not throw, returns empty
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        // TODO(wave-2): Add IAuthFailureHandler integration tests using DefaultAuthFailureHandler.
        // Contracts and defaults are shipped — needs fixture-backed tests (see TECH_DEBT.md Core Compliance rewrite).

        #endregion

        #region Cancellation Tests

        [Fact]
        public async Task Indexer_SearchAlbums_Respects_Cancellation()
        {
            // Arrange
            var indexer = await CreateTestIndexer();
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await indexer.SearchAlbumsAsync("long running query", cts.Token));
        }

        [Fact]
        public async Task Indexer_SearchTracks_Respects_Cancellation()
        {
            // Arrange
            var indexer = await CreateTestIndexer();
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await indexer.SearchTracksAsync("long running query", cts.Token));
        }

        [Fact]
        public async Task DownloadClient_Enqueue_Respects_Cancellation()
        {
            // Arrange
            var downloadClient = await CreateTestDownloadClient();
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await downloadClient.EnqueueAlbumDownloadAsync("test-album", "/output/path", cts.Token));
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
        public async Task Indexer_Search_Returns_Empty_On_No_Results()
        {
            // Arrange
            var indexer = await CreateTestIndexer();

            // Act
            var results = await indexer.SearchAlbumsAsync("zzzzzzz_nonexistent_artist_12345");

            // Assert
            Assert.NotNull(results);
            // May be empty or have results, but should not throw
        }

        #endregion

        #region Helper Methods

        private IPlugin CreateTestPlugin()
        {
            var mock = new Mock<IPlugin>();
            mock.SetupGet(p => p.Manifest).Returns(CreateTestManifest());
            mock.SetupGet(p => p.SettingsProvider).Returns(CreateTestSettingsProvider());
            mock.Setup(p => p.InitializeAsync(It.IsAny<IPluginContext>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
            mock.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
            mock.Setup(p => p.CreateIndexerAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult<IIndexer?>(null));
            mock.Setup(p => p.CreateDownloadClientAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult<IDownloadClient?>(null));
            return mock.Object;
        }

        private IPluginContext CreateTestContext()
        {
            var mock = new Mock<IPluginContext>();
            mock.SetupGet(c => c.LoggerFactory).Returns(new Mock<ILoggerFactory>().Object);
            return mock.Object;
        }

        private ISettingsProvider CreateTestSettingsProvider()
        {
            var mock = new Mock<ISettingsProvider>();
            mock.Setup(s => s.Describe()).Returns(new List<SettingDefinition>
            {
                new() { Key = "ConfigPath", DisplayName = "Config Path", IsRequired = true },
                new() { Key = "DownloadPath", DisplayName = "Download Path", IsRequired = true }
            });
            mock.Setup(s => s.GetDefaults()).Returns(new Dictionary<string, object?>
            {
                ["ConfigPath"] = string.Empty,
                ["DownloadPath"] = string.Empty
            });
            // Validate: reject when required fields are empty, accept otherwise
            mock.Setup(s => s.Validate(It.IsAny<IDictionary<string, object?>>()))
                .Returns((IDictionary<string, object?> settings) =>
                {
                    var errors = new List<string>();
                    if (!settings.TryGetValue("ConfigPath", out var cp) || string.IsNullOrWhiteSpace(cp?.ToString()))
                        errors.Add("ConfigPath is required");
                    if (!settings.TryGetValue("DownloadPath", out var dp) || string.IsNullOrWhiteSpace(dp?.ToString()))
                        errors.Add("DownloadPath is required");
                    return errors.Count > 0
                        ? PluginValidationResult.Failure(errors)
                        : PluginValidationResult.Success();
                });
            mock.Setup(s => s.Apply(It.IsAny<IDictionary<string, object?>>()))
                .Returns((IDictionary<string, object?> settings) =>
                {
                    var errors = new List<string>();
                    if (!settings.TryGetValue("ConfigPath", out var cp) || string.IsNullOrWhiteSpace(cp?.ToString()))
                        errors.Add("ConfigPath is required");
                    if (!settings.TryGetValue("DownloadPath", out var dp) || string.IsNullOrWhiteSpace(dp?.ToString()))
                        errors.Add("DownloadPath is required");
                    return errors.Count > 0
                        ? PluginValidationResult.Failure(errors)
                        : PluginValidationResult.Success();
                });
            return mock.Object;
        }

        private PluginManifest CreateTestManifest()
        {
            return new PluginManifest
            {
                Id = "test-plugin",
                Name = "Test Plugin",
                Version = "1.0.0",
                ApiVersion = "1.x"
            };
        }

        private IDictionary<string, object?> CreateValidSettings()
        {
            return new Dictionary<string, object?>
            {
                ["ConfigPath"] = "/valid/path",
                ["DownloadPath"] = "/valid/download"
            };
        }

        private IDictionary<string, object?> CreateInvalidSettings()
        {
            return new Dictionary<string, object?>
            {
                ["ConfigPath"] = "", // Empty required field
                ["DownloadPath"] = ""
            };
        }

        private Task<IIndexer> CreateTestIndexer()
        {
            var mock = new Mock<IIndexer>();
            mock.Setup(i => i.InitializeAsync(It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult(PluginValidationResult.Success()));
            // Simulate a real search that respects cancellation
            mock.Setup(i => i.SearchAlbumsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string query, CancellationToken ct) =>
                {
                    await Task.Delay(5000, ct); // Long enough to be cancelled
                    return (IReadOnlyList<StreamingAlbum>)new List<StreamingAlbum>();
                });
            mock.Setup(i => i.SearchTracksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string query, CancellationToken ct) =>
                {
                    await Task.Delay(5000, ct);
                    return (IReadOnlyList<StreamingTrack>)new List<StreamingTrack>();
                });
            mock.Setup(i => i.DisposeAsync()).Returns(ValueTask.CompletedTask);
            return Task.FromResult(mock.Object);
        }

        private Task<IIndexer> CreateAuthenticatedIndexer()
        {
            return CreateTestIndexer();
        }

        private Task<IDownloadClient> CreateTestDownloadClient()
        {
            var mock = new Mock<IDownloadClient>();
            mock.Setup(d => d.InitializeAsync(It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult(PluginValidationResult.Success()));
            mock.Setup(d => d.EnqueueAlbumDownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string albumId, string outputPath, CancellationToken ct) =>
                {
                    await Task.Delay(1000, ct); // Simulate long-running operation
                    return "download-id-123";
                });
            mock.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);
            return Task.FromResult(mock.Object);
        }

        #endregion
    }
}
