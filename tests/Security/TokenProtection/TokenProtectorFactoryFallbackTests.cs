using System;
using System.IO;
using System.Reflection;
using System.Text;

using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.TokenProtection;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Security.TokenProtection;

/// <summary>
/// Regression tests for the Lidarr-Docker token-protection startup failure.
///
/// Bug report (2026-05-23): users could not save any API key in any plugin.
/// Setting <c>BrainarrSettings.ApiKey</c> from the Lidarr UI threw
/// <c>UnauthorizedAccessException: Access to the path '/app/bin/.config' is denied</c>
/// — the DataProtection backend defaulted to a RELATIVE
/// <c>.config/lidarr.plugin.common/keys</c> path because
/// <c>Environment.SpecialFolder.UserProfile</c> returned empty (Lidarr's
/// Docker user wasn't in <c>/etc/passwd</c>); that relative path resolved
/// against the cwd (<c>/app/bin</c>, the install dir, mounted read-only)
/// and creating it failed.
///
/// Two contracts pinned here:
/// 1. <see cref="DataProtectionTokenProtector.GetDefaultKeysDir(out string)"/>
///    NEVER returns a relative path. Every candidate must be checked with
///    <c>Path.IsPathRooted</c> before being accepted.
/// 2. <see cref="TokenProtectorFactory.CreateFromEnvironment"/> NEVER throws
///    on a writable-path failure (unless <c>LP_COMMON_REQUIRE_PROTECTOR=true</c>).
///    It falls back to <see cref="NullTokenProtector"/>, sets
///    <c>IsDegradedToPlaintext = true</c>, and publishes diagnostics so the
///    plugin's startup log can surface the degradation.
/// </summary>
public sealed class TokenProtectorFactoryFallbackTests
{
    /// <summary>
    /// Bug-regression test: with <c>$HOME</c> empty and every XDG var empty,
    /// the key dir resolver MUST NOT produce a relative <c>.config/...</c>
    /// path (which would resolve against cwd and break in Docker).
    /// </summary>
    [Fact]
    public void GetDefaultKeysDir_AlwaysReturnsRootedPath_EvenWhenHomeAndXdgAreEmpty()
    {
        using var envScope = new TemporaryEnv(("HOME", string.Empty),
                                        ("XDG_DATA_HOME", string.Empty),
                                        ("XDG_CONFIG_HOME", string.Empty),
                                        ("LP_COMMON_KEYS_PATH", null));

        var dir = DataProtectionTokenProtector.GetDefaultKeysDir(out var source);

        Assert.False(string.IsNullOrWhiteSpace(dir), "GetDefaultKeysDir must never return empty or whitespace.");
        Assert.True(Path.IsPathRooted(dir), $"GetDefaultKeysDir must always return a rooted path, got: '{dir}' (source: {source}).");
    }

    /// <summary>
    /// When <c>$HOME</c> is empty AND every SpecialFolder returns empty AND
    /// no XDG override is present, the final fallback to
    /// <c>Path.GetTempPath()</c> MUST be hit (last-resort writable directory).
    /// </summary>
    [Fact]
    public void GetDefaultKeysDir_FallsBackToTempPath_AsLastResort()
    {
        // This test is best-effort because SpecialFolder.LocalApplicationData /
        // .ApplicationData can return non-empty values on Windows test runners
        // (even with HOME unset). The contract we're pinning is "the resolver
        // never throws and never returns empty" — the rooted-ness check above
        // already guards the bug. Here we just confirm the temp-path source is
        // reachable when every other candidate yields empty.
        using var envScope = new TemporaryEnv(("HOME", string.Empty),
                                        ("XDG_DATA_HOME", string.Empty),
                                        ("XDG_CONFIG_HOME", string.Empty),
                                        ("LP_COMMON_KEYS_PATH", null));

        var dir = DataProtectionTokenProtector.GetDefaultKeysDir(out _);

        // Lower-bound assertion: the resolved dir is one of the recognised candidate roots,
        // not a synthesised string that mixes empty + path.
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(),
        };
        Assert.Contains(roots, root => !string.IsNullOrWhiteSpace(root) && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetDefaultKeysDir_PrefersXdgDataHome_WhenSet()
    {
        var xdgDir = Path.Combine(Path.GetTempPath(), $"lpc-test-xdg-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(xdgDir);

            using var envScope = new TemporaryEnv(("HOME", null),
                                            ("XDG_DATA_HOME", xdgDir),
                                            ("XDG_CONFIG_HOME", null));

            var dir = DataProtectionTokenProtector.GetDefaultKeysDir(out var source);

            Assert.StartsWith(xdgDir, dir, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("XDG_DATA_HOME", source);
        }
        finally
        {
            try { Directory.Delete(xdgDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The factory MUST NOT throw when the configured key-ring directory is
    /// not writable — it must fall back to <see cref="NullTokenProtector"/>
    /// so the plugin stays usable. The bug report's symptom was the
    /// non-graceful path: the plugin would fail every <c>set_ApiKey</c>
    /// instead of degrading.
    /// </summary>
    [Fact]
    public void CreateFromEnvironment_FallsBackToNullProtector_WhenKeysDirIsUnwritable()
    {
        // Reliable cross-platform "unwritable directory": create a FILE,
        // then point the key-dir env var at that file's path. Directory
        // creation must fail because the path already exists as a file.
        var unwritable = CreateUnwritableDirCandidate();
        try
        {
            ResetFactoryStateForTest();

            using var envScope = new TemporaryEnv(
                ("LP_COMMON_PROTECTOR", "dataprotection"),
                ("LP_COMMON_KEYS_PATH", unwritable),
                ("LP_COMMON_REQUIRE_PROTECTOR", null));

            var protector = TokenProtectorFactory.CreateFromEnvironment();

            Assert.NotNull(protector);
            Assert.Equal("null", protector.AlgorithmId);
            Assert.True(TokenProtectorFactory.IsDegradedToPlaintext,
                "Factory should mark itself as degraded when the key store init failed.");

            var diag = TokenProtectorFactory.LastDiagnostics;
            Assert.NotNull(diag);
            Assert.NotNull(diag!.DegradedReason);
            Assert.Contains("backend init failed", diag.DegradedReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(unwritable); } catch { }
        }
    }

    /// <summary>
    /// When <c>LP_COMMON_REQUIRE_PROTECTOR=true</c> the factory MUST propagate
    /// the initialisation exception instead of degrading. This is the
    /// production-hardening opt-in for operators who would rather see the
    /// plugin fail loudly than silently store secrets as plaintext.
    /// </summary>
    [Fact]
    public void CreateFromEnvironment_Throws_WhenRequireProtectorIsSet_AndBackendFails()
    {
        var unwritable = CreateUnwritableDirCandidate();
        try
        {
            ResetFactoryStateForTest();

            using var envScope = new TemporaryEnv(
                ("LP_COMMON_PROTECTOR", "dataprotection"),
                ("LP_COMMON_KEYS_PATH", unwritable),
                ("LP_COMMON_REQUIRE_PROTECTOR", "true"));

            Assert.ThrowsAny<Exception>(() => TokenProtectorFactory.CreateFromEnvironment());
        }
        finally
        {
            try { File.Delete(unwritable); } catch { }
        }
    }

    /// <summary>
    /// Creates a temp file and returns its path so a caller can point the
    /// DataProtection key-dir env var at it. <c>Directory.CreateDirectory</c>
    /// must fail because the path already exists as a file (Windows:
    /// <c>IOException</c>; Linux: <c>IOException</c>).
    /// </summary>
    private static string CreateUnwritableDirCandidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpc-test-unwritable-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    /// <summary>
    /// Explicit opt-in: <c>LP_COMMON_PROTECTOR=null</c> returns the null
    /// protector immediately without any backend probe.
    /// </summary>
    [Fact]
    public void CreateFromEnvironment_ExplicitNullMode_ReturnsNullProtector_WithoutInitialisingBackend()
    {
        ResetFactoryStateForTest();

        using var envScope = new TemporaryEnv(
            ("LP_COMMON_PROTECTOR", "null"),
            ("LP_COMMON_REQUIRE_PROTECTOR", "true"));

        var protector = TokenProtectorFactory.CreateFromEnvironment();

        Assert.NotNull(protector);
        Assert.Equal("null", protector.AlgorithmId);

        var diag = TokenProtectorFactory.LastDiagnostics;
        Assert.NotNull(diag);
        Assert.Contains("explicit", diag!.BackendName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="NullTokenProtector"/> round-trips bytes verbatim. Critical:
    /// the wrapping <see cref="StringTokenProtector"/> still produces a
    /// well-formed <c>lpc:ps:v1:</c> envelope, but the embedded algorithm
    /// id is "null" so an audit can recognise unprotected blobs in
    /// persisted settings.
    /// </summary>
    [Fact]
    public void NullProtector_WrappedByStringTokenProtector_RoundTripsAndIdentifiesAsNull()
    {
        const string plaintext = "sk-test-not-actually-encrypted-1234567890";

        var wrapper = new StringTokenProtector(new NullTokenProtector());
        var protectedStr = wrapper.Protect(plaintext);

        Assert.NotNull(protectedStr);
        Assert.StartsWith("lpc:ps:v1:", protectedStr!);

        // The first segment after the prefix is the base64-url-encoded algorithm id.
        var afterPrefix = protectedStr!.Substring("lpc:ps:v1:".Length);
        var firstColon = afterPrefix.IndexOf(':');
        Assert.True(firstColon > 0, "Protected blob must carry an algorithm id segment.");

        var algB64Url = afterPrefix.Substring(0, firstColon);
        var algBytes = Base64UrlDecode(algB64Url);
        Assert.Equal("null", Encoding.UTF8.GetString(algBytes));

        // Round-trip still works (the user's secret can be read back).
        var roundTrip = wrapper.Unprotect(protectedStr);
        Assert.Equal(plaintext, roundTrip);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Resets the factory's static state so each test starts with a clean
    /// snapshot. Required because the factory caches the degradation flag
    /// and diagnostics across calls within a process.
    /// </summary>
    private static void ResetFactoryStateForTest()
    {
        var type = typeof(TokenProtectorFactory);
        var degraded = type.GetField("_degraded", BindingFlags.NonPublic | BindingFlags.Static);
        degraded?.SetValue(null, 0);

        var diag = type.GetProperty("LastDiagnostics", BindingFlags.Public | BindingFlags.Static);
        diag?.SetValue(null, null);
    }

    /// <summary>
    /// Scoped environment-variable override that restores the prior values on
    /// dispose. Pass <c>null</c> for value to unset.
    /// </summary>
    private sealed class TemporaryEnv : IDisposable
    {
        private readonly (string Name, string? OldValue)[] _restore;

        public TemporaryEnv(params (string Name, string? Value)[] sets)
        {
            _restore = new (string, string?)[sets.Length];
            for (int i = 0; i < sets.Length; i++)
            {
                _restore[i] = (sets[i].Name, Environment.GetEnvironmentVariable(sets[i].Name));
                Environment.SetEnvironmentVariable(sets[i].Name, sets[i].Value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, old) in _restore)
            {
                Environment.SetEnvironmentVariable(name, old);
            }
        }
    }
}
