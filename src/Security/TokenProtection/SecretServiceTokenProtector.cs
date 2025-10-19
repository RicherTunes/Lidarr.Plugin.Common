using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    /// <summary>
    /// Linux Secret Service-backed protector via 'secret-tool'. Falls back to exception if the helper is unavailable.
    /// Stores a random AES key under service 'LPC.TokenProtector' and account 'Default'.
    /// </summary>
    internal sealed class SecretServiceTokenProtector : ITokenProtector
    {
        private const string Service = "LPC.TokenProtector";
        private const string Account = "Default";
        private readonly byte[] _key;

        public SecretServiceTokenProtector()
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Secret Service is only available on Linux.");

            _key = GetOrCreateKey();
            AlgorithmId = "secret-service-aesgcm";
        }

        public string AlgorithmId { get; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            Span<byte> nonce = stackalloc byte[12];
            RandomNumberGenerator.Fill(nonce);
            var cipher = new byte[12 + plaintext.Length + 16];
            nonce.CopyTo(cipher);
            using var aesgcm = CreateAesGcm();
            aesgcm.Encrypt(nonce, plaintext, cipher.AsSpan(12, plaintext.Length), cipher.AsSpan(12 + plaintext.Length, 16));
            return cipher;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 28)
                throw new CryptographicException("Invalid protected payload.");
            var nonce = protectedBytes.Slice(0, 12);
            var tag = protectedBytes.Slice(protectedBytes.Length - 16, 16);
            var cipher = protectedBytes.Slice(12, protectedBytes.Length - 28);
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
            // Try lookup
            var existing = RunSecretTool("lookup", $"service {Service} account {Account}");
            if (existing.success && !string.IsNullOrWhiteSpace(existing.output))
            {
                try { return Convert.FromBase64String(existing.output.Trim()); } catch { /* ignore */ }
            }
            // Create new key and store
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var keyB64 = Convert.ToBase64String(key);
            var label = "LPC Token Protector";
            var storeArgs = $"store --label {EscapeArg(label)} service {EscapeArg(Service)} account {EscapeArg(Account)}";
            var res = RunSecretTool("store", storeArgs, keyB64 + "\n");
            if (!res.success)
            {
                throw new InvalidOperationException("secret-tool failed to store key. Ensure libsecret is installed and a session bus is available.");
            }
            return key;
        }

        private static (bool success, string output) RunSecretTool(string cmd, string args, string? stdin = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi) ?? throw new IOException("Failed to start secret-tool");
                if (stdin != null)
                {
                    p.StandardInput.Write(stdin);
                    p.StandardInput.Flush();
                    p.StandardInput.Close();
                }
                var output = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                return (p.ExitCode == 0, p.ExitCode == 0 ? output : err);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("secret-tool not available or failed to run", ex);
            }
        }

        private static string EscapeArg(string s) => '"' + s.Replace("\"", "\\\"") + '"';
    }
}
