using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
// NOTE: Do NOT add `using Azure.Identity;` or any Azure.* reference here.
// The compile-time AssemblyRef would force every consuming plugin's merged DLL
// to ship Azure.Identity + Azure.Extensions.AspNetCore.DataProtection.Keys
// (transitively Azure.Core, Microsoft.Identity.Client, etc.) — ~10 MB of dead
// weight when AKV isn't used (the default for every Lidarr plugin shipped today).
// AKV setup below uses reflection so the assembly load is on-demand; without an
// AKV key id the Azure assemblies are never resolved.
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
                        var keyId = new Uri(akvKeyIdentifier);
                        akvConfigured = TryConfigureAzureKeyVaultViaReflection(options, keyId);
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
            // Copy the plaintext into an owned buffer so we can zero it after IDataProtection returns.
            // IDataProtector.Protect requires byte[]; the intermediate copy is unavoidable.
            var buffer = plaintext.ToArray();
            try
            {
                return _protector.Protect(buffer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            // Input is ciphertext; intermediate byte[] is just for IDataProtector's API surface.
            // The returned plaintext is owned by the caller (cannot be zeroed here without breaking contract).
            var buffer = protectedBytes.ToArray();
            try
            {
                return _protector.Unprotect(buffer);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
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

        /// <summary>
        /// Configures `options.ProtectKeysWithAzureKeyVault(keyId, new DefaultAzureCredential())`
        /// via reflection so the Azure.Identity / Azure.Extensions.AspNetCore.DataProtection.Keys
        /// assemblies are only resolved when AKV is actually opted into. Without this indirection,
        /// every consuming plugin's merged DLL has a hard AssemblyRef to Azure.Identity and ships
        /// (or fails to load if stripped) ~10 MB of Azure SDK assemblies.
        ///
        /// Returns true when AKV configuration succeeded; false (without throwing) when the Azure
        /// assemblies aren't on disk — the caller treats that as "AKV not available" and falls back
        /// to the local DataProtection key store.
        /// </summary>
        private static bool TryConfigureAzureKeyVaultViaReflection(IDataProtectionBuilder options, Uri keyId)
        {
            // Resolve `Azure.Identity.DefaultAzureCredential`. Assembly load is on-demand and
            // returns null cleanly when the DLL isn't present (rather than throwing FileNotFound).
            var credentialType = Type.GetType("Azure.Identity.DefaultAzureCredential, Azure.Identity", throwOnError: false);
            if (credentialType is null) return false;

            // The extension method lives in Microsoft.AspNetCore.DataProtection.AzureKeyVault.AzureDataProtectionBuilderExtensions
            // (shipped by Azure.Extensions.AspNetCore.DataProtection.Keys). Three overloads exist;
            // we want the (IDataProtectionBuilder, Uri, TokenCredential) one. Bind by name +
            // signature shape (parameter count + first parameter type) to be tolerant of refactors.
            var extType = Type.GetType(
                "Microsoft.AspNetCore.DataProtection.AzureKeyVault.AzureDataProtectionBuilderExtensions, Azure.Extensions.AspNetCore.DataProtection.Keys",
                throwOnError: false);
            if (extType is null) return false;

            MethodInfo? extMethod = null;
            foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, "ProtectKeysWithAzureKeyVault", StringComparison.Ordinal)) continue;
                var ps = m.GetParameters();
                if (ps.Length != 3) continue;
                if (ps[0].ParameterType != typeof(IDataProtectionBuilder)) continue;
                if (ps[1].ParameterType != typeof(Uri)) continue;
                // Third parameter is Azure.Core.TokenCredential — match by name to avoid taking
                // a hard dep on Azure.Core.dll just to grab the type.
                if (ps[2].ParameterType.FullName != "Azure.Core.TokenCredential") continue;
                extMethod = m;
                break;
            }
            if (extMethod is null) return false;

            var credential = Activator.CreateInstance(credentialType);
            extMethod.Invoke(null, new object?[] { options, keyId, credential });
            return true;
        }
    }
}
