using System;
using System.Security.Cryptography;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
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
            return ProtectedData.Protect(plaintext.ToArray(), optionalEntropy: null, scope: _scope);
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            return ProtectedData.Unprotect(protectedBytes.ToArray(), optionalEntropy: null, scope: _scope);
        }
    }
}

