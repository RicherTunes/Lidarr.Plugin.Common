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
            // Exercise concurrent saves across processes.
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
        public async Task LoadAsync_NullSessionInFile_ReturnsNull_DoesNotThrow()
        {
            // Regression (harden campaign): a malformed token file whose Session field is null made
            // the TokenEnvelope ctor throw ArgumentNullException, which ESCAPED the
            // IOException/JsonException catch filter and propagated out of LoadAsync. A malformed
            // envelope must be treated as "no token" (return null).
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));
            try
            {
                var file = Path.Combine(dir.FullName, "tokens.json");
                await File.WriteAllTextAsync(file, "{\"Session\":null,\"ExpiresAt\":\"2099-01-01T00:00:00+00:00\"}");
                var store = CreateFileStore(file);

                var loaded = await store.LoadAsync();

                Assert.Null(loaded); // pre-fix: threw ArgumentNullException
            }
            finally
            {
                try { Directory.Delete(dir.FullName, true); } catch { }
            }
        }

        [Fact]
        public async Task RapidSequentialSaves_ReleaseLockEachTime_NoStallOrThrow()
        {
            // Regression: the Windows cross-process lock was a thread-affine Mutex whose
            // ReleaseMutex ran on an await-continuation / cancellation thread and failed
            // (silently swallowed), leaving the lock held so later saves stalled (~120s) or
            // relied on abandoned-mutex recovery. A thread-agnostic FileStream lock releases
            // cleanly from any thread, so back-to-back saves stay fast.
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));
            try
            {
                var file = Path.Combine(dir.FullName, "tokens.json");
                var store = CreateFileStore(file);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 25; i++)
                {
                    var env = new TokenEnvelope<SessionDto>(new SessionDto { AccessToken = $"tok{i}", RefreshToken = $"ref{i}" }, DateTime.UtcNow.AddMinutes(10));
                    await store.SaveAsync(env);
                }
                sw.Stop();

                // A release-on-wrong-thread failure would leave the lock held and stall the next
                // acquire; 25 prompt saves must finish well under the lock's acquire timeout.
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"25 sequential saves took {sw.Elapsed} — cross-process lock likely not released between saves");

                var loaded = await store.LoadAsync();
                Assert.NotNull(loaded);
                Assert.Equal("tok24", loaded!.Session.AccessToken);
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

        [Fact]
        public async Task Saves_In_Protected_Format()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n")));
            try
            {
                var file = Path.Combine(dir.FullName, "tokens.json");
                var store = CreateFileStore(file);
                var env = new TokenEnvelope<SessionDto>(new SessionDto { AccessToken = "secret-token", RefreshToken = "secret-ref" }, DateTime.UtcNow.AddMinutes(3));
                await store.SaveAsync(env);

                var text = await File.ReadAllTextAsync(file);
                Assert.Contains("\"v\": 2", text);
                Assert.Contains("payload", text);
                Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
                Assert.DoesNotContain("AccessToken", text, StringComparison.Ordinal);

                var loaded = await store.LoadAsync();
                Assert.NotNull(loaded);
                Assert.Equal("secret-token", loaded!.Session.AccessToken);
            }
            finally
            {
                try { Directory.Delete(dir.FullName, true); } catch { }
            }
        }
    }
}
