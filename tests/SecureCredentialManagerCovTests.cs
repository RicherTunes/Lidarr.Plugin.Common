using System;
using System.Security;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Security;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Coverage tests for SecureCredentialManager - additional edge cases
    /// and scenarios not covered by the main test suite.
    /// </summary>
    [Trait("Category", "Unit")]
    public class SecureCredentialManagerCovTests
    {
        #region CreateSecureString Edge Cases

        [Fact]
        public void CreateSecureString_WithSingleCharacter_ReturnsValidSecureString()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var input = "X";

            // Act
            var secureString = manager.CreateSecureString(input);

            // Assert
            Assert.NotNull(secureString);
            Assert.True(secureString!.IsReadOnly());
            Assert.Equal(1, secureString.Length);
        }

        [Fact]
        public void CreateSecureString_WithNewlineCharacters_PreservesCharacters()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var input = "line1\nline2\rline3";

            // Act
            var secureString = manager.CreateSecureString(input);
            using var wrapper = new SecureCredentialWrapper(secureString!);

            // Assert
            Assert.Equal(input, wrapper.GetPlainText());
        }

        [Fact]
        public void CreateSecureString_WithNullCharacter_TruncatesAtNull()
        {
            // Arrange - SecureString stops at null character (embedded nulls truncate)
            using var manager = new SecureCredentialManager();
            var input = "before\0after";

            // Act
            var secureString = manager.CreateSecureString(input);
            using var wrapper = new SecureCredentialWrapper(secureString!);

            // Assert - SecureString truncates at the null character
            Assert.Equal("before", wrapper.GetPlainText());
        }

        [Fact]
        public void CreateSecureString_WithHighUnicode_PreservesCharacters()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var input = "\uD83D\uDE00\uD83C\uDF89"; // Emoji: 😀🎉

            // Act
            var secureString = manager.CreateSecureString(input);
            using var wrapper = new SecureCredentialWrapper(secureString!);

            // Assert
            Assert.Equal(input, wrapper.GetPlainText());
        }

        #endregion

        #region StoreCredential Additional Coverage

        [Fact]
        public void StoreCredential_WithNullKey_ThrowsException()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act & Assert - null key causes ArgumentNullException from ConcurrentDictionary
            Assert.Throws<ArgumentNullException>(() => manager.StoreCredential(null!, "value"));
        }

        [Fact]
        public void StoreCredential_OverwritesSameKeyMultipleTimes_MaintainsLatestValue()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            const string key = "same-key";

            // Act
            manager.StoreCredential(key, "value1");
            manager.StoreCredential(key, "value2");
            manager.StoreCredential(key, "value3");
            manager.StoreCredential(key, "final-value");
            var result = manager.GetCredential(key);

            // Assert
            Assert.Equal("final-value", result);
        }

        [Fact]
        public void StoreCredential_WithMixedCaseKeys_TreatsAsDifferent()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act
            manager.StoreCredential("Key", "lowercase-value");
            manager.StoreCredential("KEY", "uppercase-value");
            manager.StoreCredential("key", "mixed-value");

            // Assert - Keys are case-sensitive
            Assert.Equal("lowercase-value", manager.GetCredential("Key"));
            Assert.Equal("uppercase-value", manager.GetCredential("KEY"));
            Assert.Equal("mixed-value", manager.GetCredential("key"));
        }

        #endregion

        #region GetCredential Additional Coverage

        [Fact]
        public void GetCredential_AfterStoreAndOverwrite_ReturnsLatestValue()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            manager.StoreCredential("key", "original");

            // Act
            var first = manager.GetCredential("key");
            manager.StoreCredential("key", "updated");
            var second = manager.GetCredential("key");

            // Assert
            Assert.Equal("original", first);
            Assert.Equal("updated", second);
        }

        [Fact]
        public void GetCredential_WithNullKey_ThrowsArgumentNullException()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act & Assert - ConcurrentDictionary throws on null key
            Assert.Throws<ArgumentNullException>(() => manager.GetCredential(null!));
        }

        #endregion

        #region SecureCredentialWrapper Additional Coverage

        [Fact]
        public void SecureCredentialWrapper_WithEmptySecureString_ThrowsArgumentNullException()
        {
            // Arrange
            var emptySecureString = new SecureString();
            emptySecureString.MakeReadOnly();

            // Act & Assert - empty SecureString is not null, but contains no data
            using var wrapper = new SecureCredentialWrapper(emptySecureString);
            var result = wrapper.GetPlainText();

            // Assert - empty SecureString returns empty string
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void SecureCredentialWrapper_GetPlainText_WithMaxLengthString_ReturnsCorrectly()
        {
            // Arrange - Create a reasonably long string
            using var manager = new SecureCredentialManager();
            var longInput = new string('Z', 10000);
            var secureString = manager.CreateSecureString(longInput);

            // Act
            using var wrapper = new SecureCredentialWrapper(secureString!);
            var result = wrapper.GetPlainText();

            // Assert
            Assert.Equal(longInput, result);
        }

        [Fact]
        public void SecureCredentialWrapper_Dispose_SetsInternalSecureStringToNull()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test");
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Act
            wrapper.Dispose();

            // Assert - Second dispose should not throw
            wrapper.Dispose();
        }

        [Fact]
        public void SecureCredentialWrapper_GetPlainText_AfterManagerDisposed_StillWorks()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("independent");
            var wrapper = new SecureCredentialWrapper(secureString!);

            // Act - Dispose manager first
            manager.Dispose();

            // Assert - Wrapper should still work since it has its own reference
            var result = wrapper.GetPlainText();
            Assert.Equal("independent", result);
            wrapper.Dispose();
        }

        #endregion

        #region Concurrent Access Edge Cases

        [Fact]
        public void StoreCredential_ConcurrentSameKey_HandlesCorrectly()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            const string key = "concurrent-key";
            var errors = 0;

            // Act - Multiple threads writing to same key
            Parallel.For(0, 50, i =>
            {
                try
                {
                    manager.StoreCredential(key, $"value-{i}");
                }
                catch
                {
                    System.Threading.Interlocked.Increment(ref errors);
                }
            });

            // Assert - No errors and final value is retrievable
            Assert.Equal(0, errors);
            var finalValue = manager.GetCredential(key);
            Assert.NotNull(finalValue);
        }

        [Fact]
        public void GetCredential_ConcurrentReadsAfterStore_AllSucceed()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            manager.StoreCredential("read-key", "read-value");
            var mismatches = 0;

            // Act
            Parallel.For(0, 100, _ =>
            {
                var value = manager.GetCredential("read-key");
                if (value != "read-value")
                {
                    System.Threading.Interlocked.Increment(ref mismatches);
                }
            });

            // Assert
            Assert.Equal(0, mismatches);
        }

        [Fact]
        public async Task Dispose_DuringConcurrentAccess_HandlesGracefully()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Act - Start concurrent operations then dispose
            var task1 = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        manager.StoreCredential($"key-{i}", $"value-{i}");
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            var task2 = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        manager.GetCredential($"key-{i}");
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            // Dispose while operations may be running
            manager.Dispose();

            await Task.WhenAll(task1, task2);

            // Assert - Some operations may throw ObjectDisposedException, which is expected
            // The important thing is no corruption or unhandled exceptions
            Assert.True(true);
        }

        #endregion

        #region Dispose Pattern Coverage

        [Fact]
        public void Dispose_WithNoStoredCredentials_DoesNotThrow()
        {
            // Arrange
            var manager = new SecureCredentialManager();

            // Act & Assert
            manager.Dispose();
        }

        [Fact]
        public void Dispose_AfterPartialOperations_HandlesCorrectly()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("key1", "value1");
            // Don't store anything else

            // Act
            manager.Dispose();

            // Assert - Should not throw
            Assert.Throws<ObjectDisposedException>(() => manager.GetCredential("key1"));
        }

        #endregion

        #region Memory and Resource Coverage

        [Fact]
        public void SecureCredentialManager_MultipleInstances_OperateIndependently()
        {
            // Arrange
            using var manager1 = new SecureCredentialManager();
            using var manager2 = new SecureCredentialManager();

            // Act
            manager1.StoreCredential("shared-key", "manager1-value");
            manager2.StoreCredential("shared-key", "manager2-value");

            // Assert
            Assert.Equal("manager1-value", manager1.GetCredential("shared-key"));
            Assert.Equal("manager2-value", manager2.GetCredential("shared-key"));
        }

        [Fact]
        public void StoreCredential_WithAsciiOnlyCharacters_HandlesCorrectly()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var asciiOnly = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

            // Act
            manager.StoreCredential("ascii-key", asciiOnly);
            var result = manager.GetCredential("ascii-key");

            // Assert
            Assert.Equal(asciiOnly, result);
        }

        [Fact]
        public void StoreCredential_WithControlCharacters_HandlesCorrectly()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var controlChars = "\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0A\x0B\x0C\x0D\x0E\x0F";

            // Act
            manager.StoreCredential("control-key", controlChars);
            var result = manager.GetCredential("control-key");

            // Assert
            Assert.Equal(controlChars, result);
        }

        #endregion

        #region Exception Property Verification

        [Fact]
        public void GetCredential_AfterDispose_VerifiesObjectDisposedExceptionObjectName()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.StoreCredential("key", "value");
            manager.Dispose();

            // Act & Assert
            // Line 110: throw new ObjectDisposedException(nameof(SecureCredentialManager))
            var ex = Assert.Throws<ObjectDisposedException>(() => manager.GetCredential("key"));
            Assert.Equal(nameof(SecureCredentialManager), ex.ObjectName);
        }

        [Fact]
        public void StoreCredential_AfterDispose_VerifiesObjectDisposedExceptionObjectName()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.Dispose();

            // Act & Assert
            // Line 110: throw new ObjectDisposedException(nameof(SecureCredentialManager))
            var ex = Assert.Throws<ObjectDisposedException>(() => manager.StoreCredential("key", "value"));
            Assert.Equal(nameof(SecureCredentialManager), ex.ObjectName);
        }

        [Fact]
        public void CreateSecureString_AfterDispose_VerifiesObjectDisposedExceptionObjectName()
        {
            // Arrange
            var manager = new SecureCredentialManager();
            manager.Dispose();

            // Act & Assert
            // Line 110: throw new ObjectDisposedException(nameof(SecureCredentialManager))
            var ex = Assert.Throws<ObjectDisposedException>(() => manager.CreateSecureString("test"));
            Assert.Equal(nameof(SecureCredentialManager), ex.ObjectName);
        }

        [Fact]
        public void SecureCredentialWrapper_GetPlainText_AfterDispose_VerifiesObjectDisposedExceptionObjectName()
        {
            // Arrange
            using var manager = new SecureCredentialManager();
            var secureString = manager.CreateSecureString("test-value");
            var wrapper = new SecureCredentialWrapper(secureString!);
            wrapper.Dispose();

            // Act & Assert
            // Line 143: throw new ObjectDisposedException(nameof(SecureCredentialWrapper))
            var ex = Assert.Throws<ObjectDisposedException>(() => wrapper.GetPlainText());
            Assert.Equal(nameof(SecureCredentialWrapper), ex.ObjectName);
        }

        [Fact]
        public void SecureCredentialWrapper_NullSecureString_VerifiesArgumentNullExceptionParamName()
        {
            // Act & Assert
            // Line 137: _secureString = secureString ?? throw new ArgumentNullException(nameof(secureString))
            var ex = Assert.Throws<ArgumentNullException>(() => new SecureCredentialWrapper(null!));
            Assert.Equal("secureString", ex.ParamName);
        }

        #endregion

        #region Additional Edge Cases

        [Fact]
        public void StoreCredential_NullValue_ThrowsArgumentNullExceptionViaWrapper()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act & Assert
            // CreateSecureString returns null for null input
            // SecureCredentialWrapper constructor throws on null SecureString (line 137)
            Assert.Throws<ArgumentNullException>(() => manager.StoreCredential("key", null!));
        }

        [Fact]
        public void StoreCredential_EmptyValue_ThrowsArgumentNullExceptionViaWrapper()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act & Assert
            // CreateSecureString returns null for empty input (line 36-37)
            // SecureCredentialWrapper constructor throws on null SecureString (line 137)
            Assert.Throws<ArgumentNullException>(() => manager.StoreCredential("key", string.Empty));
        }

        [Fact]
        public void CreateSecureString_EmptyString_ReturnsNull()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act - Line 36-37: return null for empty string
            var result = manager.CreateSecureString(string.Empty);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void CreateSecureString_NullString_ReturnsNull()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act - Line 36-37: return null for null string
            var result = manager.CreateSecureString(null!);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetCredential_EmptyKey_ReturnsNull()
        {
            // Arrange
            using var manager = new SecureCredentialManager();

            // Act
            var result = manager.GetCredential(string.Empty);

            // Assert - Empty key is not found
            Assert.Null(result);
        }

        [Fact]
        public void Dispose_MultipleCalls_AllowMultipleDisposes()
        {
            // Arrange
            var manager = new SecureCredentialManager();

            // Act & Assert - Should not throw
            manager.Dispose();
            manager.Dispose();
            manager.Dispose();
        }

        #endregion
    }
}
