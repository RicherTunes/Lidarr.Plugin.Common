// <copyright file="FileTokenStoreProperties.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;

namespace Lidarr.Plugin.Common.Tests.Properties
{
    /// <summary>
    /// FsCheck property tests for <see cref="FileTokenStore{TSession}"/>.
    /// </summary>
    public class FileTokenStoreProperties
    {
        public sealed class TestSession
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public int Version { get; set; }
        }

        private static string NewTempFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "LPC.FileTokenStoreProps." + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "tokens.json");
        }

        private static void TryCleanup(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Round-trip: <c>SaveAsync(envelope); LoadAsync()</c> reproduces the original
        /// session payload across the encrypt/persist/decrypt boundary.
        /// </summary>
        [Property(MaxTest = 25)]
        public bool SaveThenLoad_PreservesSession(
            NonNull<string> access,
            NonNull<string> refresh,
            int version)
        {
            var path = NewTempFile();
            try
            {
                var store = new FileTokenStore<TestSession>(path);
                var session = new TestSession
                {
                    AccessToken = access.Get,
                    RefreshToken = refresh.Get,
                    Version = version,
                };
                var envelope = new TokenEnvelope<TestSession>(session, DateTime.UtcNow.AddMinutes(10));

                store.SaveAsync(envelope).GetAwaiter().GetResult();

                // Fresh instance forces re-read from disk + decrypt path.
                var fresh = new FileTokenStore<TestSession>(path);
                var loaded = fresh.LoadAsync().GetAwaiter().GetResult();

                return loaded != null
                    && loaded.Session.AccessToken == access.Get
                    && loaded.Session.RefreshToken == refresh.Get
                    && loaded.Session.Version == version;
            }
            finally
            {
                TryCleanup(path);
            }
        }

        /// <summary>
        /// Encryption-at-rest: persisted file contents must not contain the raw access token.
        /// The secret is built deterministically from random bytes so every iteration produces
        /// a non-trivial alphanumeric token where a literal substring match is meaningful.
        /// </summary>
        [Property(MaxTest = 20)]
        public bool PersistedFile_DoesNotContainRawSecret(byte[] seed)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new System.Text.StringBuilder("propsecret-");
            // Always at least 24 chars after the prefix; deterministic from seed.
            var length = 24 + (seed.Length % 8);
            for (var i = 0; i < length; i++)
            {
                var idx = seed.Length == 0 ? i : (seed[i % seed.Length] + i) % alphabet.Length;
                sb.Append(alphabet[idx]);
            }
            var secret = sb.ToString();

            var path = NewTempFile();
            try
            {
                var store = new FileTokenStore<TestSession>(path);
                var envelope = new TokenEnvelope<TestSession>(
                    new TestSession { AccessToken = secret, RefreshToken = "rt", Version = 1 },
                    DateTime.UtcNow.AddMinutes(10));

                store.SaveAsync(envelope).GetAwaiter().GetResult();

                var contents = File.ReadAllText(path);
                return !contents.Contains(secret);
            }
            finally
            {
                TryCleanup(path);
            }
        }

        /// <summary>
        /// Idempotent saves: saving the same envelope twice yields a file that still loads
        /// to the same session payload (no corruption from temp-file/replace race).
        /// </summary>
        [Property(MaxTest = 15)]
        public bool DoubleSave_LoadsSameSession(NonNull<string> access)
        {
            var path = NewTempFile();
            try
            {
                var store = new FileTokenStore<TestSession>(path);
                var envelope = new TokenEnvelope<TestSession>(
                    new TestSession { AccessToken = access.Get, RefreshToken = "rt", Version = 7 },
                    DateTime.UtcNow.AddMinutes(10));

                store.SaveAsync(envelope).GetAwaiter().GetResult();
                store.SaveAsync(envelope).GetAwaiter().GetResult();

                var loaded = store.LoadAsync().GetAwaiter().GetResult();
                return loaded != null
                    && loaded.Session.AccessToken == access.Get
                    && loaded.Session.Version == 7;
            }
            finally
            {
                TryCleanup(path);
            }
        }
    }
}
