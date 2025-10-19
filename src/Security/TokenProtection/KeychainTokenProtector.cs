using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    internal sealed class KeychainTokenProtector : ITokenProtector
    {
        private const string Service = "LPC.TokenProtector";
        private const string Account = "Default";
        private readonly byte[] _key; // 32-byte AES key

        public KeychainTokenProtector()
        {
            if (!OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("Keychain is only available on macOS.");

            _key = GetOrCreateKey();
            AlgorithmId = "keychain-aesgcm";
        }

        public string AlgorithmId { get; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            Span<byte> nonce = stackalloc byte[12];
            RandomNumberGenerator.Fill(nonce);
            var cipher = new byte[12 + plaintext.Length + 16]; // nonce + ciphertext + tag
            nonce.CopyTo(cipher);
            using var aesgcm = CreateAesGcm();
            aesgcm.Encrypt(nonce, plaintext, cipher.AsSpan(12, plaintext.Length), cipher.AsSpan(12 + plaintext.Length, 16));
            return cipher;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 12 + 16)
                throw new CryptographicException("Invalid protected payload.");
            var nonce = protectedBytes.Slice(0, 12);
            var tag = protectedBytes.Slice(protectedBytes.Length - 16, 16);
            var cipher = protectedBytes.Slice(12, protectedBytes.Length - 12 - 16);
            var plain = new byte[cipher.Length];
            using var aesgcm = CreateAesGcm();
            aesgcm.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }

        private AesGcm CreateAesGcm()
        {
#if NET8_0_OR_GREATER
            return new AesGcm(_key, 16);
#else
            return new AesGcm(_key);
#endif
        }

        private static byte[] GetOrCreateKey()
        {
            // Try to load existing
            if (TryFindGenericPassword(Service, Account, out var existing))
            {
                return existing;
            }
            // Create and store new 32-byte key
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            if (!AddGenericPassword(Service, Account, key))
            {
                throw new InvalidOperationException("Failed to persist key in Keychain.");
            }
            return key;
        }

        private static bool TryFindGenericPassword(string service, string account, out byte[] key)
        {
            key = Array.Empty<byte>();
            IntPtr pwdPtr = IntPtr.Zero;
            IntPtr itemRef = IntPtr.Zero;
            try
            {
                int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    out uint length, out pwdPtr, out itemRef);
                if (status == 0 && length > 0 && pwdPtr != IntPtr.Zero)
                {
                    key = new byte[length];
                    Marshal.Copy(pwdPtr, key, 0, (int)length);
                    return true;
                }
                return false;
            }
            finally
            {
                if (pwdPtr != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, pwdPtr);
                if (itemRef != IntPtr.Zero) CFRelease(itemRef);
            }
        }

        private static bool AddGenericPassword(string service, string account, ReadOnlySpan<byte> secret)
        {
            IntPtr itemRef;
            int status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                (uint)secret.Length, secret.ToArray(), out itemRef);
            if (itemRef != IntPtr.Zero) CFRelease(itemRef);
            return status == 0;
        }

        [DllImport("Security", EntryPoint = "SecKeychainFindGenericPassword")]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, string serviceName, uint accountNameLength, string accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport("Security", EntryPoint = "SecKeychainAddGenericPassword")]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, string serviceName, uint accountNameLength, string accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

        [DllImport("Security", EntryPoint = "SecKeychainItemFreeContent")]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport("CoreFoundation", EntryPoint = "CFRelease")]
        private static extern void CFRelease(IntPtr cf);
    }
}
