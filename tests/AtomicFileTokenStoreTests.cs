using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class AtomicFileTokenStoreTests
    {
        private sealed class SessionDto
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
        }

        private static ITokenStore<SessionDto> CreateFileStore(string path)
            => new Lidarr.Plugin.Common.Services.Authentication.FileTokenStore<SessionDto>(path, null, null);

        [Fact]
        public async Task Concurrent_Saves_Do_Not_Corrupt_File()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));
            try
            {
                var file = Path.Combine(dir.FullName, "tokens.json");
                var s1 = CreateFileStore(file);
                var s2 = CreateFileStore(file);

                var env1 = new TokenEnvelope<SessionDto>(new SessionDto { AccessToken = "tok1", RefreshToken = "ref1" }, DateTime.UtcNow.AddMinutes(10));
                var env2 = new TokenEnvelope<SessionDto>(new SessionDto { AccessToken = "tok2", RefreshToken = "ref2" }, DateTime.UtcNow.AddMinutes(5));

                await Task.WhenAll(
                    Task.Run(() => s1.SaveAsync(env1)),
                    Task.Run(() => s2.SaveAsync(env2))
                );

                var loaded = await s1.LoadAsync();
                Assert.NotNull(loaded);
                Assert.NotNull(loaded!.Session);
                Assert.True(loaded.Session.AccessToken == "tok1" || loaded.Session.AccessToken == "tok2");
            }
            finally
            {
                try { Directory.Delete(dir.FullName, true); } catch { }
            }
        }

        [Fact]
        public async Task Ignores_Stale_Temp_File()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));
            try
            {
                var file = Path.Combine(dir.FullName, "tokens.json");
                var store = CreateFileStore(file);
                var env = new TokenEnvelope<SessionDto>(new SessionDto { AccessToken = "tok", RefreshToken = "ref" }, DateTime.UtcNow.AddMinutes(2));
                await store.SaveAsync(env);

                // Simulate previous crash leaving behind a stale temp file with partial (invalid) content
                await File.WriteAllTextAsync(file + ".tmp", "{not-json}");

                var loaded = await store.LoadAsync();
                Assert.NotNull(loaded);
                Assert.Equal("tok", loaded!.Session.AccessToken);
            }
            finally
            {
                try { Directory.Delete(dir.FullName, true); } catch { }
            }
        }
    }
}
