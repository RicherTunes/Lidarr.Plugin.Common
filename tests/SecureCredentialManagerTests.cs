using System;
using System.Security;
using Lidarr.Plugin.Common.Security;
using Xunit;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class SecureCredentialManagerTests
    {
        #region StoreAndGetCredential Tests

        [Fact]
        public void StoreAndGetCredential_RoundTrip()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var key = "test-api-key";
            var credential = "my-secret-credential-value";

            // Act
            manager.StoreCredential(key, credential);
            var retrieved = manager.GetCredential(key);

            // Assert
            Assert.Equal(credential, retrieved);
        }

        [Fact]
        public void GetCredential_ReturnsNullForMissingKey()
        {
            // Arrange
            var manager = new SecureCredentialManager();

            // Act
            var result = manager.GetCredential("non-existent-key");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void StoreCredential_ReplacesExistingCredential()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var key = "api-key";
            var originalValue = "original-credential";
            var newValue = "new-credential-value";

            // Act
            manager.StoreCredential(key, originalValue);
            var firstRetrieve = manager.GetCredential(key);
            manager.StoreCredential(key, newValue);
            var secondRetrieve = manager.GetCredential(key);

            // Assert
            Assert.Equal(originalValue, firstRetrieve);
            Assert.Equal(newValue, secondRetrieve);
        }

        [Fact]
        public void StoreCredential_MultipleKeys_ReturnCorrectValues()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var credentials = new[]
            {
                ("key1", "value1"),
                ("key2", "value2"),
                ("key3", "value3")
            };

            // Act
            foreach (var (key, value) in credentials)
            {
                manager.StoreCredential(key, value);
            }

            // Assert
            foreach (var (key, value) in credentials)
            {
                Assert.Equal(value, manager.GetCredential(key));
            }
        }

        [Fact]
        public void StoreAndGetCredential_WithSpecialCharacters()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var key = "special-chars-key";
            var credential = "p@ssw0rd!#$%^&*()_+-=[]{}|;':\",./<>?`~";

            // Act
            manager.StoreCredential(key, credential);
            var retrieved = manager.GetCredential(key);

            // Assert
            Assert.Equal(credential, retrieved);
        }

        [Fact]
        public void StoreAndGetCredential_WithUnicodeCharacters()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var key = "unicode-key";
            var credential = "密码パスワード비밀번호";

            // Act
            manager.StoreCredential(key, credential);
            var retrieved = manager.GetCredential(key);

            // Assert
            Assert.Equal(credential, retrieved);
        }

        [Fact]
        public void StoreAndGetCredential_EmptyString_ThrowsArgumentNullException()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var key = "empty-key";
            var credential = string.Empty;

            // Act & Assert
            // Empty string results in null SecureString, which throws when wrapped
            Assert.Throws<ArgumentNullException>(() => manager.StoreCredential(key, credential));
        }

        #endregion

        #region CreateSecureString Tests

        [Fact]
        public void CreateSecureString_ValidInput_ReturnsSecureString()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var input = "my-secure-data";

            // Act
            var secureString = manager.CreateSecureString(input);

            // Assert
            Assert.NotNull(secureString);
            Assert.True(secureString.IsReadOnly());
        }

        [Fact]
        public void CreateSecureString_NullInput_ReturnsNull()
        {
            // Arrange
            var manager = new SecureCredentialManager();

            // Act
            var secureString = manager.CreateSecureString(null!);

            // Assert
            Assert.Null(secureString);
        }

        [Fact]
        public void CreateSecureString_EmptyInput_ReturnsNull()
        {
            // Arrange
            var manager = new SecureCredentialManager();

            // Act
            var secureString = manager.CreateSecureString(string.Empty);

            // Assert
            Assert.Null(secureString);
        }

        [Fact]
        public void CreateSecureString_ProducesReadOnlySecureString()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var input = "test-data";

            // Act
            var secureString = manager.CreateSecureString(input);

            // Assert
            Assert.True(secureString!.IsReadOnly());
        }

        #endregion

        #region Dispose Tests

        [Fact]
        public void Dispose_DisposesAllCredentials()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("key1", "value1");
            manager.StoreCredential("key2", "value2");

            // Act
            manager.Dispose();

            // Assert - Getting credentials after dispose should throw
            Assert.Throws<ObjectDisposedException>(() => manager.GetCredential("key1"));
        }

        [Fact]
        public void Dispose_MultipleDisposeCalls_DoNotThrow()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("key", "value");

            // Act & Assert - Should not throw
            manager.Dispose();
            manager.Dispose();
        }

        [Fact]
        public void OperationsAfterDispose_ThrowObjectDisposed()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("key", "value");
            manager.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => manager.GetCredential("key"));
            Assert.Throws<ObjectDisposedException>(() => manager.StoreCredential("key2", "value2"));
            Assert.Throws<ObjectDisposedException>(() => manager.CreateSecureString("test"));
        }

        [Fact]
        public void Dispose_ClearsCredentialStorage()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("key1", "value1");
            manager.StoreCredential("key2", "value2");

            // Act
            manager.Dispose();

            // Assert - After dispose, the dictionary should be cleared
            // This is tested by the fact that subsequent calls throw ObjectDisposedException
            Assert.Throws<ObjectDisposedException>(() => manager.GetCredential("key1"));
            Assert.Throws<ObjectDisposedException>(() => manager.GetCredential("key2"));
        }

        #endregion

        #region SecureCredentialWrapper Tests

        [Fact]
        public void SecureCredentialWrapper_GetPlainText_ReturnsCorrectValue()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test-value");
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Act
            var plainText = wrapper.GetPlainText();

            // Assert
            Assert.Equal("test-value", plainText);
        }

        [Fact]
        public void SecureCredentialWrapper_GetPlainText_CanBeCalledMultipleTimes()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test-value");
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Act
            var value1 = wrapper.GetPlainText();
            var value2 = wrapper.GetPlainText();

            // Assert
            Assert.Equal("test-value", value1);
            Assert.Equal("test-value", value2);
        }

        [Fact]
        public void SecureCredentialWrapper_Dispose_PreventsGetPlainText()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test-value");
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Act
            wrapper.Dispose();

            // Assert
            Assert.Throws<ObjectDisposedException>(() => wrapper.GetPlainText());
        }

        [Fact]
        public void SecureCredentialWrapper_MultipleDisposeCalls_DoNotThrow()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test-value");
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Act & Assert - Should not throw
            wrapper.Dispose();
            wrapper.Dispose();
        }

        [Fact]
        public void SecureCredentialWrapper_NullSecureString_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new SecureCredentialWrapper(null!));
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public void StoreCredential_ConcurrentOperations_DoNotThrow()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Act
            System.Threading.Tasks.Parallel.For(0, 100, i =>
            {
                try
                {
                    manager.StoreCredential($"key-{i}", $"value-{i}");
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            // Assert
            Assert.Empty(exceptions);
        }

        [Fact]
        public void GetCredential_ConcurrentReads_DoNotThrow()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("shared-key", "shared-value");
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Act
            System.Threading.Tasks.Parallel.For(0, 100, i =>
            {
                try
                {
                    var value = manager.GetCredential("shared-key");
                    Assert.Equal("shared-value", value);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            // Assert
            Assert.Empty(exceptions);
        }

        [Fact]
        public void StoreAndGetCredential_ConcurrentOperations_MaintainConsistency()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var errors = 0;

            // Act
            System.Threading.Tasks.Parallel.For(0, 50, i =>
            {
                manager.StoreCredential($"key-{i}", $"value-{i}");
                var retrieved = manager.GetCredential($"key-{i}");
                if (retrieved != $"value-{i}")
                {
                    System.Threading.Interlocked.Increment(ref errors);
                }
            });

            // Assert
            Assert.Equal(0, errors);
        }

        #endregion

        #region Long Credential Tests

        [Fact]
        public void StoreAndGetCredential_LongCredential_WorksCorrectly()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var longCredential = new string('A', 10000); // 10KB credential

            // Act
            manager.StoreCredential("long-key", longCredential);
            var retrieved = manager.GetCredential("long-key");

            // Assert
            Assert.Equal(longCredential, retrieved);
        }

        [Fact]
        public void CreateSecureString_LongInput_WorksCorrectly()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var longInput = new string('B', 5000);

            // Act
            var secureString = manager.CreateSecureString(longInput);

            // Assert
            Assert.NotNull(secureString);
            Assert.True(secureString!.IsReadOnly());

            // Verify by wrapping and getting plain text
            var wrapper = new SecureCredentialWrapper(secureString);
            Assert.Equal(longInput, wrapper.GetPlainText());
        }

        #endregion

        #region Empty and Whitespace Tests

        [Theory]
        [InlineData(" ")]
        [InlineData("  ")]
        [InlineData("\t")]
        [InlineData("\n")]
        [InlineData("\r\n")]
        public void StoreAndGetCredential_WhitespaceCredential_WorksCorrectly(string whitespace)
        {
            // Arrange
            var manager = new SecureCredentialManager();

            // Act
            manager.StoreCredential("whitespace-key", whitespace);
            var retrieved = manager.GetCredential("whitespace-key");

            // Assert
            Assert.Equal(whitespace, retrieved);
        }

        #endregion

        #region IDispose Pattern Tests

        [Fact]
        public void SecureCredentialManager_ImplementsIDisposable()
        {
            // Arrange & Act
            var manager = new SecureCredentialManager();

            // Assert
            Assert.IsAssignableFrom<IDisposable>(manager);
        }

        [Fact]
        public void SecureCredentialWrapper_ImplementsIDisposable()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test");

            // Act
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Assert
            Assert.IsAssignableFrom<IDisposable>(wrapper);
        }

        #endregion

        #region Using Statement Tests

        [Fact]
        public void SecureCredentialManager_UsingStatement_CleansUpCorrectly()
        {
            // Arrange & Act
            using (var manager = new SecureCredentialManager())
            {
                manager.StoreCredential("key", "value");
                Assert.Equal("value", manager.GetCredential("key"));
            }

            // After using block, manager is disposed
            // This test verifies no exception is thrown during disposal
            Assert.True(true);
        }

        [Fact]
        public void SecureCredentialWrapper_UsingStatement_CleansUpCorrectly()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test-value");

            // Act & Assert
            using (var wrapper = new SecureCredentialWrapper(secureString!))
            {
                Assert.Equal("test-value", wrapper.GetPlainText());
            }

            // After using block, wrapper is disposed
            // This test verifies no exception is thrown during disposal
            Assert.True(true);
        }

        #endregion
    }
}
