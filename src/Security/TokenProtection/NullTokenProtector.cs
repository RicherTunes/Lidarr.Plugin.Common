using System;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    /// <summary>
    /// Pass-through "protector" that returns plaintext bytes verbatim. Used
    /// as a graceful-degradation fallback by
    /// <see cref="TokenProtectorFactory.CreateFromEnvironment"/> when the
    /// configured backend (DataProtection, DPAPI, Keychain, Secret Service)
    /// fails to initialise — typically because the key-ring directory is not
    /// writable (Lidarr Docker container with broken <c>$HOME</c> and the
    /// install dir mounted read-only).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This protector does NOT encrypt anything.</strong> It exists
    /// so the plugin remains usable when proper protection can't initialise,
    /// rather than throwing on every <c>set_ApiKey</c> and bricking the
    /// settings UI. The wrapping <see cref="StringTokenProtector"/> records
    /// <see cref="AlgorithmId"/> as <c>"null"</c> in the protected blob, so
    /// the on-disk format is identifiable as unprotected at a glance
    /// (<c>lpc:ps:v1:bnVsbA:...</c> — base64-url("null") prefix).
    /// </para>
    /// <para>
    /// Operators who want hard-failure instead of graceful degradation should
    /// set <c>LP_COMMON_REQUIRE_PROTECTOR=true</c>; the factory will then
    /// propagate the initialisation exception rather than substituting this
    /// type.
    /// </para>
    /// </remarks>
    internal sealed class NullTokenProtector : ITokenProtector
    {
        public string AlgorithmId => "null";

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            return protectedBytes.ToArray();
        }
    }
}
