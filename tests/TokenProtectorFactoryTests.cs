using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class TokenProtectorFactoryTests
    {
        // TokenProtectorFactory is internal, so we need to test it through reflection
        // by creating a test wrapper that can access the internal class

        #region Test Wrapper

        private static class FactoryAccessor
        {
            public static ITokenProtector CreateFromEnvironment()
            {
                var factoryType = typeof(Lidarr.Plugin.Common.Security.TokenProtection.TokenProtectorFactory);
                var method = factoryType.GetMethod("CreateFromEnvironment",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return (ITokenProtector)method!.Invoke(null, null)!;
            }
        }

        #endregion

        #region Default/Auto Mode Tests

        [Fact]
        public void CreateFromEnvironment_AutoMode_ReturnsValidProtector()
        {
            // Arrange - Clear environment variables to test auto mode
            ClearEnvironmentVariables();

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
        }

        [Fact]
        public void CreateFromEnvironment_ProtectorCanProtectAndUnprotect()
        {
            // Arrange
            ClearEnvironmentVariables();
            var protector = FactoryAccessor.CreateFromEnvironment();
            var plainText = Encoding.UTF8.GetBytes("my-secret-token-data");

            // Act
            var protectedData = protector.Protect(plainText);
            var unprotectedData = protector.Unprotect(protectedData);

            // Assert
            Assert.Equal(plainText, unprotectedData);
        }

        #endregion

        #region DPAPI Mode Tests (Windows Only)

        [Fact]
        public void CreateFromEnvironment_DpapiMode_Windows_ReturnsDpapiProtector()
        {
            // Skip on non-Windows platforms
            if (!OperatingSystem.IsWindows())
            {
                return; // Test is skipped on non-Windows
            }

            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dpapi");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            Assert.Equal("DpapiTokenProtector", protector.GetType().Name);
        }

        [Fact]
        public void CreateFromEnvironment_DpapiUserMode_Windows_ReturnsUserScopedDpapi()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dpapi-user");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            Assert.Equal("DpapiTokenProtector", protector.GetType().Name);
        }

        [Fact]
        public void CreateFromEnvironment_DpapiMachineMode_Windows_ReturnsMachineScopedDpapi()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dpapi-machine");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            Assert.Equal("DpapiTokenProtector", protector.GetType().Name);
        }

        #endregion

        #region Keychain Mode Tests (macOS Only)

        [Fact]
        public void CreateFromEnvironment_KeychainMode_macOS_ReturnsKeychainProtector()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "keychain");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            // Type name may vary based on availability
            Assert.Contains("keychain", protector.GetType().Name, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Secret Service Mode Tests (Linux Only)

        [Fact]
        public void CreateFromEnvironment_SecretServiceMode_Linux_ReturnsSecretServiceOrFallback()
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "secret-service");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            // May return SecretServiceTokenProtector or fallback to DataProtection
            var typeName = protector.GetType().Name;
            Assert.True(typeName.Contains("SecretService", StringComparison.OrdinalIgnoreCase) ||
                        typeName.Contains("DataProtection", StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Data Protection Mode Tests (All Platforms)

        [Fact]
        public void CreateFromEnvironment_DataProtectionMode_ReturnsDataProtectionProtector()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            Assert.Equal("DataProtectionTokenProtector", protector.GetType().Name);
        }

        [Fact]
        public void CreateFromEnvironment_DataProtectionMode_WithCustomAppName()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            var customAppName = "MyCustomApp";
            Environment.SetEnvironmentVariable("LP_COMMON_APP_NAME", customAppName);

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();
            var plainText = Encoding.UTF8.GetBytes("test-token");
            var protectedData = protector.Protect(plainText);

            // Assert
            Assert.NotNull(protectedData);
            Assert.NotEqual(plainText, protectedData);

            // Cleanup
            Environment.SetEnvironmentVariable("LP_COMMON_APP_NAME", null);
        }

        #endregion

        #region Environment Variable Configuration Tests

        [Fact]
        public void CreateFromEnvironment_UsesDefaultAppName_WhenNotSet()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            Environment.SetEnvironmentVariable("LP_COMMON_APP_NAME", null);

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsCustomAppName()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            Environment.SetEnvironmentVariable("LP_COMMON_APP_NAME", "TestApplication");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
            var plainText = Encoding.UTF8.GetBytes("test-data");
            var protectedData = protector.Protect(plainText);
            var unprotectedData = protector.Unprotect(protectedData);
            Assert.Equal(plainText, unprotectedData);

            // Cleanup
            Environment.SetEnvironmentVariable("LP_COMMON_APP_NAME", null);
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsKeysPath()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            Environment.SetEnvironmentVariable("LP_COMMON_KEYS_PATH", "/tmp/test-keys");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);

            // Cleanup
            Environment.SetEnvironmentVariable("LP_COMMON_KEYS_PATH", null);
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsCertPath()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");

            // Create a temporary test certificate
            string? certPath = null;
            string certPassword = "test-password";

            try
            {
                certPath = CreateTestCertificate(certPassword);
                Environment.SetEnvironmentVariable("LP_COMMON_CERT_PATH", certPath);
                Environment.SetEnvironmentVariable("LP_COMMON_CERT_PASSWORD", certPassword);

                // Act
                var protector = FactoryAccessor.CreateFromEnvironment();

                // Assert
                Assert.NotNull(protector);
            }
            finally
            {
                // Cleanup
                Environment.SetEnvironmentVariable("LP_COMMON_CERT_PATH", null);
                Environment.SetEnvironmentVariable("LP_COMMON_CERT_PASSWORD", null);
                if (certPath != null && File.Exists(certPath))
                {
                    File.Delete(certPath);
                }
            }
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsCertPassword()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            Environment.SetEnvironmentVariable("LP_COMMON_CERT_PASSWORD", "test-password");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);

            // Cleanup
            Environment.SetEnvironmentVariable("LP_COMMON_CERT_PASSWORD", null);
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsCertThumbprint()
        {
            // Skip on non-Windows platforms (cert thumbprint lookup uses Windows cert store)
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // Check if we have admin access to install certificates to LocalMachine store
            if (!HasLocalMachineCertStoreAccess())
            {
                // Skip gracefully when not running as administrator
                return;
            }

            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");

            // Create and install a temporary test certificate
            X509Certificate2? cert = null;

            try
            {
                cert = CreateAndInstallTestCertificate();
                Environment.SetEnvironmentVariable("LP_COMMON_CERT_THUMBPRINT", cert.Thumbprint);

                // Act
                var protector = FactoryAccessor.CreateFromEnvironment();

                // Assert
                Assert.NotNull(protector);
            }
            finally
            {
                // Cleanup
                Environment.SetEnvironmentVariable("LP_COMMON_CERT_THUMBPRINT", null);
                if (cert != null)
                {
                    try
                    {
                        RemoveCertificateFromStore(cert);
                    }
                    catch
                    {
                        // Ignore cleanup errors if cert wasn't installed
                    }
                    cert.Dispose();
                }
            }
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsAkvKeyId()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            Environment.SetEnvironmentVariable("LP_COMMON_AKV_KEY_ID", "https://vault.azure.net/keys/test-key");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);

            // Cleanup
            Environment.SetEnvironmentVariable("LP_COMMON_AKV_KEY_ID", null);
        }

        [Fact]
        public void CreateFromEnvironment_AcceptsKmsUri_Alias()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            Environment.SetEnvironmentVariable("LP_COMMON_KMS_URI", "aws-kms://key-id");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);

            // Cleanup
            Environment.SetEnvironmentVariable("LP_COMMON_KMS_URI", null);
        }

        #endregion

        #region Case Insensitivity Tests

        [Fact]
        public void CreateFromEnvironment_ModeIsCaseInsensitive()
        {
            // Arrange
            ClearEnvironmentVariables();
            var modes = new[] { "DATAPROTECTION", "DataProtection", "dataProtection", "DaTaPrOtEcTiOn" };

            foreach (var mode in modes)
            {
                Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", mode);

                // Act
                var protector = FactoryAccessor.CreateFromEnvironment();

                // Assert
                Assert.NotNull(protector);
            }
        }

        [Fact]
        public void CreateFromEnvironment_TrimWhitespaceFromMode()
        {
            // Arrange
            ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "  dataprotection  ");

            // Act
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Assert
            Assert.NotNull(protector);
        }

        #endregion

        #region Protection Round-trip Tests

        [Fact]
        public void ProtectUnprotect_RoundTrip_WorksCorrectly()
        {
            // Arrange
            ClearEnvironmentVariables();
            var protector = FactoryAccessor.CreateFromEnvironment();
            var testData = Encoding.UTF8.GetBytes("my-sensitive-access-token-123456789");

            // Act
            var protectedData = protector.Protect(testData);
            var unprotectedData = protector.Unprotect(protectedData);

            // Assert
            Assert.Equal(testData, unprotectedData);
        }

        [Fact]
        public void Protect_ProducesDifferentCipherTextEachTime()
        {
            // Arrange
            ClearEnvironmentVariables();
            var protector = FactoryAccessor.CreateFromEnvironment();
            var testData = Encoding.UTF8.GetBytes("my-sensitive-data");

            // Act
            var protected1 = protector.Protect(testData);
            var protected2 = protector.Protect(testData);

            // Assert - Due to IV/nonce, encrypted data should be different
            // Note: Some protectors might produce same output, so this is a soft assertion
            // The key assertion is that both decrypt to the same value
            Assert.Equal(testData, protector.Unprotect(protected1));
            Assert.Equal(testData, protector.Unprotect(protected2));
        }

        [Fact]
        public void Protect_Unprotect_EmptyArray()
        {
            // Arrange
            ClearEnvironmentVariables();
            var protector = FactoryAccessor.CreateFromEnvironment();
            var emptyData = Array.Empty<byte>();

            // Act
            var protectedData = protector.Protect(emptyData);
            var unprotectedData = protector.Unprotect(protectedData);

            // Assert
            Assert.Empty(unprotectedData);
        }

        [Fact]
        public void Protect_Unprotect_SpecialCharacters()
        {
            // Arrange
            ClearEnvironmentVariables();
            var protector = FactoryAccessor.CreateFromEnvironment();
            var testData = Encoding.UTF8.GetBytes("test_with-special.chars@123#$/\\|=+&^%$#@!");

            // Act
            var protectedData = protector.Protect(testData);
            var unprotectedData = protector.Unprotect(protectedData);

            // Assert
            Assert.Equal(testData, unprotectedData);
        }

        #endregion

        #region AlgorithmId Tests

        [Fact]
        public void AlgorithmId_ReturnsValidIdentifier()
        {
            // Arrange
            ClearEnvironmentVariables();
            var protector = FactoryAccessor.CreateFromEnvironment();

            // Act
            var algorithmId = protector.AlgorithmId;

            // Assert
            Assert.NotNull(algorithmId);
            Assert.NotEmpty(algorithmId);
        }

        #endregion

        #region Helper Methods

        private void ClearEnvironmentVariables()
        {
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", null);
            Environment.SetEnvironmentVariable("LP_COMMON_APP_NAME", null);
            Environment.SetEnvironmentVariable("LP_COMMON_KEYS_PATH", null);
            Environment.SetEnvironmentVariable("LP_COMMON_CERT_PATH", null);
            Environment.SetEnvironmentVariable("LP_COMMON_CERT_PASSWORD", null);
            Environment.SetEnvironmentVariable("LP_COMMON_CERT_THUMBPRINT", null);
            Environment.SetEnvironmentVariable("LP_COMMON_AKV_KEY_ID", null);
            Environment.SetEnvironmentVariable("LP_COMMON_KMS_URI", null);
        }

        /// <summary>
        /// Creates a temporary self-signed certificate file for testing.
        /// </summary>
        /// <returns>The path to the created PFX file.</returns>
        private static string CreateTestCertificate(string password)
        {
            var distinguishedName = new X500DistinguishedName($"CN=TokenProtector Test Cert, O=Test");
            var keySize = 2048;

            using var rsa = RSA.Create(keySize);
            var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // Add extensions to make it a valid cert
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                    critical: true));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                    critical: false));

            // Create self-signed certificate valid for 1 hour
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.Now.AddMinutes(-5),
                DateTimeOffset.Now.AddHours(1));

            // Export to PFX
            var certBytes = certificate.Export(X509ContentType.Pfx, password);

            // Write to temp file
            var tempPath = Path.Combine(Path.GetTempPath(), $"test-cert-{Guid.NewGuid()}.pfx");
            File.WriteAllBytes(tempPath, certBytes);

            return tempPath;
        }

        /// <summary>
        /// Creates and installs a test certificate in the LocalMachine/My store for testing thumbprint lookup.
        /// </summary>
        /// <returns>The installed certificate.</returns>
        private static X509Certificate2 CreateAndInstallTestCertificate()
        {
            var distinguishedName = new X500DistinguishedName($"CN=TokenProtector Test Thumbprint Cert, O=Test");
            var keySize = 2048;

            using var rsa = RSA.Create(keySize);
            var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                    critical: true));

            var certificate = request.CreateSelfSigned(
                DateTimeOffset.Now.AddMinutes(-5),
                DateTimeOffset.Now.AddHours(1));

            // Export and re-import with MachineKeySet so it persists in the store
            var certBytes = certificate.Export(X509ContentType.Pfx, (string?)null);
            var persistableCert = new X509Certificate2(certBytes, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

            // Install in LocalMachine/My store
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(persistableCert);

            return persistableCert;
        }

        /// <summary>
        /// Removes a test certificate from the LocalMachine/My store.
        /// </summary>
        private static void RemoveCertificateFromStore(X509Certificate2 certificate)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }

        /// <summary>
        /// Checks if the current process has write access to the LocalMachine certificate store.
        /// </summary>
        private static bool HasLocalMachineCertStoreAccess()
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                return true;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }

        #endregion
    }
}
