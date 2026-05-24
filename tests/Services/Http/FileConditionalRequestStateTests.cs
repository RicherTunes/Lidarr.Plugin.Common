using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Hosting;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http
{
    /// <summary>
    /// Tests for <see cref="FileConditionalRequestState"/>.
    /// </summary>
    [Trait("Category", "Unit")]
    public class FileConditionalRequestStateTests : IDisposable
    {
        private readonly string _tempFolder;

        public FileConditionalRequestStateTests()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "fcrs-test-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempFolder, recursive: true); }
            catch { }
        }

        [Fact]
        public async Task ExplicitFolder_RoundTripsValidators()
        {
            // Arrange
            var state = new FileConditionalRequestState(_tempFolder);
            var key = "endpoint:GET:/v1/catalog/us/albums/12345";
            var etag = "\"deadbeef\"";
            var lastMod = DateTimeOffset.UtcNow.AddMinutes(-5);

            // Act
            await state.SetValidatorsAsync(key, etag, lastMod);
            var loaded = await state.TryGetValidatorsAsync(key);

            // Assert
            Assert.NotNull(loaded);
            Assert.Equal(etag, loaded!.Value.ETag);
            Assert.Equal(lastMod, loaded.Value.LastModified);
        }

        [Fact]
        public async Task TryGetValidatorsAsync_ReturnsNullForMissingKey()
        {
            // Arrange
            var state = new FileConditionalRequestState(_tempFolder);

            // Act
            var result = await state.TryGetValidatorsAsync("nope");

            // Assert
            Assert.Null(result);
        }

        /// <summary>
        /// Regression test for the Docker bug where Lidarr's container has empty HOME, so the
        /// old chain <c>Path.Combine(Environment.GetFolderPath(SpecialFolder.ApplicationData), ...)</c>
        /// resolved to a relative path anchored at the read-only /app/bin cwd. The constructor
        /// must now defer to <see cref="PluginConfigRoots.Resolve"/> so the resulting folder is
        /// always absolute (rooted) and writable.
        /// </summary>
        [Fact]
        public async Task Constructor_DefaultFolder_ResolvesToRootedPath()
        {
            // Arrange: pin the resolver to a known absolute root via the explicit override env var.
            var overrideRoot = Path.Combine(Path.GetTempPath(), "fcrs-resolver-test-" + Guid.NewGuid().ToString("N"));
            var previousOverride = Environment.GetEnvironmentVariable(PluginConfigRoots.OverrideEnvVar);
            Environment.SetEnvironmentVariable(PluginConfigRoots.OverrideEnvVar, overrideRoot);
            try
            {
                // Act: construct with no explicit folder so the default branch runs through
                // PluginConfigRoots.Resolve("ArrPlugins") and lands under <override>/ArrPlugins/etag-validators.
                var state = new FileConditionalRequestState(folder: null);

                // Round-trip a validator to prove the resolved path is rooted and writable.
                // If the constructor had picked up a relative path, this would have failed in
                // Lidarr's Docker container with UnauthorizedAccessException.
                var key = "smoke:/api/resolver";
                await state.SetValidatorsAsync(key, "\"abc\"", DateTimeOffset.UtcNow);
                var loaded = await state.TryGetValidatorsAsync(key);

                // Assert
                Assert.NotNull(state);
                Assert.NotNull(loaded);
                Assert.Equal("\"abc\"", loaded!.Value.ETag);

                var expectedRoot = Path.Combine(overrideRoot, "ArrPlugins", "etag-validators");
                Assert.True(Path.IsPathRooted(expectedRoot));
                Assert.True(Directory.Exists(expectedRoot), $"Expected resolver-derived root '{expectedRoot}' to exist.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(PluginConfigRoots.OverrideEnvVar, previousOverride);
                try { Directory.Delete(overrideRoot, recursive: true); }
                catch { }
            }
        }
    }
}
