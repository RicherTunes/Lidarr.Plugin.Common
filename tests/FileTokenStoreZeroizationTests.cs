using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// F-02: FileTokenStore must zeroize the plaintext token buffers it owns (the base64-decoded blob and the
    /// unprotected JSON on load; the serialized JSON + cipher on save) so bearer-token material doesn't linger
    /// on the managed heap until GC. Validated by capturing the buffer the protector hands back on load and
    /// asserting the store cleared it.
    /// </summary>
    public sealed class FileTokenStoreZeroizationTests
    {
        public sealed class FakeSession
        {
            public string Token { get; set; } = string.Empty;
        }

        // Pass-through protector that keeps a reference to the plaintext buffer it returns from Unprotect —
        // the exact buffer FileTokenStore deserializes from and must then zero.
        private sealed class CapturingProtector : ITokenProtector
        {
            public byte[]? LastUnprotectReturn;

            public string AlgorithmId => "capture";

            public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

            public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            {
                LastUnprotectReturn = protectedBytes.ToArray();
                return LastUnprotectReturn;
            }
        }

        [Fact]
        public async Task LoadAsync_ZeroizesUnprotectedPlaintextBuffer()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fts-zero-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "token.json");
                var protector = new CapturingProtector();
                var store = new FileTokenStore<FakeSession>(path, protector);

                await store.SaveAsync(new TokenEnvelope<FakeSession>(
                    new FakeSession { Token = "super-secret-token" }, DateTime.UtcNow.AddHours(1)));

                var loaded = await store.LoadAsync();

                Assert.NotNull(loaded);
                Assert.Equal("super-secret-token", loaded!.Session.Token); // deserialized before zeroization

                Assert.NotNull(protector.LastUnprotectReturn);
                Assert.NotEmpty(protector.LastUnprotectReturn!);
                Assert.All(protector.LastUnprotectReturn!, b => Assert.Equal(0, b)); // store zeroed it afterward
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }
    }
}
