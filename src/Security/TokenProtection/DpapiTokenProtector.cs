using System;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    [SupportedOSPlatform("windows")]
    internal sealed class DpapiTokenProtector : ITokenProtector
    {
        private readonly DataProtectionScope _scope;

        public DpapiTokenProtector(bool machineScope)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
            }
            _scope = machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
            AlgorithmId = machineScope ? "dpapi-machine" : "dpapi-user";
        }

        public string AlgorithmId { get; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            // Entropy not required; DPAPI adds integrity and confidentiality.
            // Copy plaintext into an owned buffer so we can zero it after DPAPI returns.
            var buffer = plaintext.ToArray();
            try
            {
                return ProtectedData.Protect(buffer, optionalEntropy: null, scope: _scope);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            // protectedBytes is ciphertext (already on heap as input); the returned plaintext is
            // owned by the caller and we cannot zero it here without breaking the contract.
            // The intermediate copy below is just to materialize a byte[] for DPAPI's API surface.
            var buffer = protectedBytes.ToArray();
            try
            {
                return ProtectedData.Unprotect(buffer, optionalEntropy: null, scope: _scope);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }
}
