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

        /// <summary>
        /// Resolves a writable directory for the DataProtection key ring,
        /// trying multiple candidates in order before falling back to
        /// <c>Path.GetTempPath()</c>. Designed for Lidarr Docker containers
        /// (hotio / linuxserver) where <c>$HOME</c> can be empty if the
        /// container PUID/PGID isn't reflected in <c>/etc/passwd</c> — the
        /// original implementation returned a relative <c>.config/...</c>
        /// path in that case which resolved against the cwd
        /// (<c>/app/bin</c>), a read-only mount.
        /// </summary>
        /// <remarks>
        /// Candidate order:
        /// <list type="number">
        ///   <item><description><c>$XDG_DATA_HOME</c> — the standard XDG location for persistent user data; Lidarr Docker images typically set this to <c>/config</c>.</description></item>
        ///   <item><description><c>$XDG_CONFIG_HOME</c> — the standard XDG location for user config.</description></item>
        ///   <item><description><c>Environment.SpecialFolder.LocalApplicationData</c> — on Linux, falls back to <c>~/.local/share</c> when XDG isn't set; on Windows, AppData/Local.</description></item>
        ///   <item><description><c>$HOME/.local/share</c> — explicit XDG fallback when SpecialFolder resolution is broken.</description></item>
        ///   <item><description><c>Environment.SpecialFolder.ApplicationData</c> — Windows AppData/Roaming; on Linux, <c>~/.config</c>.</description></item>
        ///   <item><description><c>$HOME/.config</c> — explicit fallback when SpecialFolder.ApplicationData returns empty.</description></item>
        ///   <item><description><c>Path.GetTempPath()</c> — LAST RESORT (writable but ephemeral). Caller emits a warning when this hits.</description></item>
        /// </list>
        /// Each candidate is only used if it produces a non-empty rooted (absolute) path. Empty or relative
        /// candidates are skipped — the bug this fixes was caused by silently accepting <c>"" + "/.config/..."</c>
        /// → a path that resolved relative to cwd.
        /// </remarks>
        internal static string GetDefaultKeysDir() => GetDefaultKeysDir(out _);

        /// <summary>
        /// Overload that reports back the candidate name that won, so the
        /// factory can emit a one-line warning when the fallback to
        /// <c>Path.GetTempPath()</c> is hit.
        /// </summary>
        internal static string GetDefaultKeysDir(out string source)
        {
            const string Leaf1 = "Lidarr.Plugin.Common";
            const string Leaf2 = "keys";

            string? candidate;

            candidate = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (IsUsableRootedDir(candidate))
            {
                source = "XDG_DATA_HOME";
                return Path.Combine(candidate!, Leaf1, Leaf2);
            }

            candidate = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (IsUsableRootedDir(candidate))
            {
                source = "XDG_CONFIG_HOME";
                return Path.Combine(candidate!, Leaf1, Leaf2);
            }

            candidate = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (IsUsableRootedDir(candidate))
            {
                source = "SpecialFolder.LocalApplicationData";
                return Path.Combine(candidate!, Leaf1, Leaf2);
            }

            // Explicit XDG-style fallback when LocalApplicationData was empty.
            // On Linux+net8, SpecialFolder.LocalApplicationData usually returns
            // $HOME/.local/share, but only when $HOME is set AND the user is
            // in /etc/passwd. If both are broken, $HOME via env may still work.
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home))
            {
                source = "$HOME/.local/share";
                return Path.Combine(home, ".local", "share", Leaf1, Leaf2);
            }

            candidate = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (IsUsableRootedDir(candidate))
            {
                source = "SpecialFolder.ApplicationData";
                return Path.Combine(candidate!, Leaf1, Leaf2);
            }

            // Final structured fallback: $HOME/.config when SpecialFolder.ApplicationData
            // is empty. Already covered $HOME above, this is just for completeness.
            if (!string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home))
            {
                source = "$HOME/.config";
                return Path.Combine(home, ".config", Leaf1, Leaf2);
            }

            // Last resort: temp dir. Always writable, always rooted, but
            // ephemeral — caller emits a warning so the operator knows the
            // key ring won't survive a container restart.
            source = "Path.GetTempPath() (ephemeral)";
            return Path.Combine(Path.GetTempPath(), Leaf1, Leaf2);
        }

        private static bool IsUsableRootedDir(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (!Path.IsPathRooted(candidate)) return false;
            return true;
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
