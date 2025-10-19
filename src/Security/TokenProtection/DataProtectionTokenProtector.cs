using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
#if NET8_0_OR_GREATER
using Azure.Identity;
#endif
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    /// <summary>
    /// IDataProtection-based protector with optional key persistence and key protection via certificate or OS-specific mechanisms.
    /// </summary>
    internal sealed class DataProtectionTokenProtector : ITokenProtector
    {
        private readonly IDataProtector _protector;
        public string AlgorithmId { get; }

        private DataProtectionTokenProtector(IDataProtector protector, string algorithmId)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            AlgorithmId = algorithmId;
        }

        public static DataProtectionTokenProtector Create(
            string applicationName,
            string? keysDirectory,
            string? certificatePath,
            string? certificatePassword,
            string? certificateThumbprint,
            string? akvKeyIdentifier = null)
        {
            var requireAkv = string.Equals(Environment.GetEnvironmentVariable("LP_COMMON_REQUIRE_AKV"), "true", StringComparison.OrdinalIgnoreCase);
            var akvConfigured = false;
            // Configure Data Protection provider and optionally wrap keys with AKV/certificate
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory ?? GetDefaultKeysDir()), options =>
            {
                options.SetApplicationName(applicationName);

                if (!string.IsNullOrWhiteSpace(certificatePath))
                {
                    using var cert = new X509Certificate2(certificatePath!, certificatePassword, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
                    options.ProtectKeysWithCertificate(cert);
                }
                else if (!string.IsNullOrWhiteSpace(certificateThumbprint))
                {
                    using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(X509FindType.FindByThumbprint, certificateThumbprint!.Replace(" ", string.Empty), validOnly: false);
                    if (certs.Count == 0)
                    {
                        throw new InvalidOperationException($"Certificate with thumbprint {certificateThumbprint} not found in LocalMachine/My store.");
                    }
                    options.ProtectKeysWithCertificate(certs[0]);
                }

                if (!string.IsNullOrWhiteSpace(akvKeyIdentifier))
                {
                    try
                    {
#if NET8_0_OR_GREATER
                        var keyId = new Uri(akvKeyIdentifier);
                        var cred = new DefaultAzureCredential();
                        options.ProtectKeysWithAzureKeyVault(keyId, cred);
                        akvConfigured = true;
#else
                        // AKV wrapping requires .NET 8+ in this library build; ignore on older TFMs
#endif
                    }
                    catch
                    {
                        // Ignore AKV hookup errors; consumer can validate AlgorithmId.
                    }
                }
            });

            var dp = provider.CreateProtector($"{applicationName}:LPC:TokenStore:v1");
            if (requireAkv && !akvConfigured && !string.IsNullOrWhiteSpace(akvKeyIdentifier))
            {
                throw new InvalidOperationException("Azure Key Vault key wrapping was requested but could not be configured. Check AKV credentials and key id.");
            }

            var alg = akvConfigured ? "dataprotection-akv" :
                      (!string.IsNullOrWhiteSpace(certificatePath) || !string.IsNullOrWhiteSpace(certificateThumbprint)) ? "dataprotection-cert" :
                      "dataprotection";
            return new DataProtectionTokenProtector(dp, alg);
        }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            return _protector.Protect(plaintext.ToArray());
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            return _protector.Unprotect(protectedBytes.ToArray());
        }

        private static string GetDefaultKeysDir()
        {
            if (OperatingSystem.IsWindows())
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appdata, "Lidarr.Plugin.Common", "keys");
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "lidarr.plugin.common", "keys");
        }
    }
}
