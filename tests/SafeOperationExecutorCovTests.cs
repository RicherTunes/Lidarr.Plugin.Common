using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Coverage tests for SafeOperationExecutor - targets uncovered paths.
    /// Source: src/Services/SafeOperationExecutor.cs
    /// </summary>
    public class SafeOperationExecutorCovTests
    {
        #region ExecuteFileOperation Tests (lines 18-55)

        [Fact]
        public void ExecuteFileOperation_NullFilePath_ReturnsFallback()
        {
            // Arrange - Test line 24-26: null filePath returns fallback
            // Proof: grep -n "string.IsNullOrWhiteSpace(filePath)" src/Services/SafeOperationExecutor.cs
            // Line 24: if (string.IsNullOrWhiteSpace(filePath))
            // Line 25-26: return fallbackValue!;
            string? nullPath = null;

            // Act
            string result = SafeOperationExecutor.ExecuteFileOperation(
                nullPath!,
                path => File.ReadAllText(path),
                "fallback");

            // Assert
            Assert.Equal("fallback", result);
        }

        [Fact]
        public void ExecuteFileOperation_EmptyFilePath_ReturnsFallback()
        {
            // Arrange - Test line 24-26: empty filePath returns fallback
            // Act
            int result = SafeOperationExecutor.ExecuteFileOperation(
                "",
                path => 42,
                -1);

            // Assert
            Assert.Equal(-1, result);
        }

        [Fact]
        public void ExecuteFileOperation_WhitespaceFilePath_ReturnsFallback()
        {
            // Arrange - Test line 24-26: whitespace filePath returns fallback
            // Act
            string result = SafeOperationExecutor.ExecuteFileOperation(
                "   ",
                path => "success",
                "fallback");

            // Assert
            Assert.Equal("fallback", result);
        }

        [Fact]
        public void ExecuteFileOperation_FileDoesNotExist_ReturnsFallback()
        {
            // Arrange - Test line 33-36: file doesn't exist returns fallback
            // Proof: grep -n "File.Exists" src/Services/SafeOperationExecutor.cs
            // Line 33: if (!File.Exists(filePath))
            // Line 35: return fallbackValue!;
            string nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}.txt");

            // Act
            string result = SafeOperationExecutor.ExecuteFileOperation(
                nonExistentPath,
                path => File.ReadAllText(path),
                "fallback");

            // Assert
            Assert.Equal("fallback", result);
        }

        [Fact]
        public void ExecuteFileOperation_SuccessfulOperation_ReturnsResult()
        {
            // Arrange - Test line 38: successful operation returns result
            // Proof: grep -n "return operation(filePath)" src/Services/SafeOperationExecutor.cs
            // Line 38: return operation(filePath);
            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "test content");

                // Act
                string result = SafeOperationExecutor.ExecuteFileOperation(
                    tempFile,
                    path => File.ReadAllText(path),
                    "fallback");

                // Assert
                Assert.Equal("test content", result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExecuteFileOperation_UnauthorizedAccessException_ReturnsFallback()
        {
            // The contract under test is "UnauthorizedAccessException => fallback". Throw it
            // deterministically: the previous read-only-file arrangement passes as root
            // (CI containers), because root bypasses permission bits and the write succeeds.
            string tempFile = Path.GetTempFileName();
            try
            {
                string result = SafeOperationExecutor.ExecuteFileOperation(
                    tempFile,
                    path => throw new UnauthorizedAccessException("denied"),
                    "fallback");

                Assert.Equal("fallback", result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExecuteFileOperation_GeneralException_ReturnsFallback()
        {
            // Arrange - Test line 48-51: general Exception returns fallback
            // Proof: grep -n "catch (Exception)" src/Services/SafeOperationExecutor.cs
            // Line 48: catch (Exception)
            // Line 50: return fallbackValue!;
            string tempFile = Path.GetTempFileName();
            try
            {
                // Act - Operation throws a general exception
                string result = SafeOperationExecutor.ExecuteFileOperation(
                    tempFile,
                    path => throw new InvalidOperationException("Test exception"),
                    "fallback");

                // Assert
                Assert.Equal("fallback", result);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExecuteFileOperation_IOExceptionRetries_ThenSucceeds()
        {
            // Arrange - Test line 40-43: IOException triggers retry
            // Proof: grep -n "IOException" src/Services/SafeOperationExecutor.cs
            // Line 40: catch (IOException) when (attempt < maxRetries)
            // Line 42: Thread.Sleep(500 * attempt);
            int callCount = 0;
            string tempFile = Path.GetTempFileName();
            try
            {
                // Act - Fail first time, succeed second time
                string result = SafeOperationExecutor.ExecuteFileOperation(
                    tempFile,
                    path =>
                    {
                        callCount++;
                        if (callCount == 1)
                        {
                            throw new IOException("Simulated IO error");
                        }
                        return "success";
                    },
                    "fallback",
                    maxRetries: 3);

                // Assert
                Assert.Equal("success", result);
                Assert.Equal(2, callCount); // Failed once, succeeded on second attempt
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void ExecuteFileOperation_MaxRetriesExhausted_ReturnsFallback()
        {
            // Arrange - Test line 54: max retries exhausted returns fallback
            // Proof: grep -n "return fallbackValue!" src/Services/SafeOperationExecutor.cs
            // Line 54 (final return): return fallbackValue!;
            int callCount = 0;
            string tempFile = Path.GetTempFileName();
            try
            {
                // Act - Always fail
                string result = SafeOperationExecutor.ExecuteFileOperation(
                    tempFile,
                    path =>
                    {
                        callCount++;
                        throw new IOException("Persistent IO error");
                    },
                    "fallback",
                    maxRetries: 2);

                // Assert
                Assert.Equal("fallback", result);
                Assert.Equal(2, callCount); // Tried maxRetries times
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        #endregion

        #region ExecuteWithTimeoutAsync Tests (lines 60-80)

        [Fact]
        public async Task ExecuteWithTimeoutAsync_SuccessfulOperation_ReturnsResult()
        {
            // Arrange - Test line 70: successful operation returns result
            // Proof: grep -n "await operation" src/Services/SafeOperationExecutor.cs
            // Line 70: return await operation(cts.Token);
            // Act
            int result = await SafeOperationExecutor.ExecuteWithTimeoutAsync(
                ct => Task.FromResult(42),
                TimeSpan.FromSeconds(1),
                -1,
                "TestOp");

            // Assert
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ExecuteWithTimeoutAsync_Timeout_ReturnsFallback()
        {
            // Arrange - Test line 72-75: OperationCanceledException when cancellation requested
            // Proof: grep -n "OperationCanceledException" src/Services/SafeOperationExecutor.cs
            // Line 72: catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            // Line 74: return fallbackValue!;
            // Act
            string result = await SafeOperationExecutor.ExecuteWithTimeoutAsync(
                async ct =>
                {
                    await Task.Delay(5000, ct); // Will be cancelled
                    return "success";
                },
                TimeSpan.FromMilliseconds(50),
                "fallback",
                "TimeoutTest");

            // Assert
            Assert.Equal("fallback", result);
        }

        [Fact]
        public async Task ExecuteWithTimeoutAsync_Exception_ReturnsFallback()
        {
            // Arrange - Test line 76-79: general Exception returns fallback
            // Proof: grep -n "catch (Exception)" src/Services/SafeOperationExecutor.cs
            // Line 76: catch (Exception)
            // Line 78: return fallbackValue!;
            // Act
            string result = await SafeOperationExecutor.ExecuteWithTimeoutAsync<string>(
                ct => throw new InvalidOperationException("Test error"),
                TimeSpan.FromSeconds(1),
                "fallback",
                "ExceptionTest");

            // Assert
            Assert.Equal("fallback", result);
        }

        #endregion

        #region TryExecute Tests (lines 86-98)

        [Fact]
        public void TryExecute_Success_ReturnsTrueAndResult()
        {
            // Arrange - Test line 90-91: success returns true and result
            // Proof: grep -n "result = operation()" src/Services/SafeOperationExecutor.cs
            // Line 90: result = operation();
            // Line 91: return true;
            // Act
            bool success = SafeOperationExecutor.TryExecute(
                () => 42,
                out int result,
                -1);

            // Assert
            Assert.True(success);
            Assert.Equal(42, result);
        }

        [Fact]
        public void TryExecute_Exception_ReturnsFalseAndFallback()
        {
            // Arrange - Test line 93-97: exception returns false and fallback
            // Proof: grep -n "catch" src/Services/SafeOperationExecutor.cs (in TryExecute)
            // Line 93-96: catch { result = fallbackValue!; return false; }
            // Act
            bool success = SafeOperationExecutor.TryExecute<int>(
                () => throw new InvalidOperationException("Test"),
                out int result,
                -1);

            // Assert
            Assert.False(success);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void TryExecute_DefaultFallback_ReturnsDefault()
        {
            // Arrange - Test default fallback value behavior
            // Act
            bool success = SafeOperationExecutor.TryExecute<int>(
                () => throw new Exception(),
                out int result);

            // Assert
            Assert.False(success);
            Assert.Equal(0, result); // default(int)
        }

        #endregion

        #region TryExecuteAsync Tests (lines 104-117)

        [Fact]
        public async Task TryExecuteAsync_Success_ReturnsTrueAndResult()
        {
            // Arrange - Test line 110-111: success returns (true, result)
            // Proof: grep -n "return (true," src/Services/SafeOperationExecutor.cs
            // Line 111: return (true, result);
            // Act
            var (success, result) = await SafeOperationExecutor.TryExecuteAsync(
                () => Task.FromResult(42),
                -1);

            // Assert
            Assert.True(success);
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task TryExecuteAsync_Exception_ReturnsFalseAndFallback()
        {
            // Arrange - Test line 114-115: exception returns (false, fallback)
            // Proof: grep -n "return (false," src/Services/SafeOperationExecutor.cs
            // Line 115: return (false, fallbackValue!);
            // Act
            var (success, result) = await SafeOperationExecutor.TryExecuteAsync<int>(
                () => throw new InvalidOperationException("Test"),
                -1);

            // Assert
            Assert.False(success);
            Assert.Equal(-1, result);
        }

        #endregion

        #region ExecuteWithFallbacks Tests (lines 122-137)

        [Fact]
        public void ExecuteWithFallbacks_FirstOperationSucceeds_ReturnsResult()
        {
            // Arrange - Test line 127-129: successful operation returns result
            // Proof: grep -n "return operation()" src/Services/SafeOperationExecutor.cs
            // Line 128: return operation();
            // Act
            string result = SafeOperationExecutor.ExecuteWithFallbacks(
                () => "first",
                () => "second",
                () => "third");

            // Assert
            Assert.Equal("first", result);
        }

        [Fact]
        public void ExecuteWithFallbacks_FirstFails_SecondSucceeds()
        {
            // Arrange - Test line 131-133: exception continues to next fallback
            // Proof: grep -n "Continue to next fallback" src/Services/SafeOperationExecutor.cs
            // Line 132: // Continue to next fallback
            // Act
            string result = SafeOperationExecutor.ExecuteWithFallbacks(
                () => throw new InvalidOperationException("First failed"),
                () => "second",
                () => "third");

            // Assert
            Assert.Equal("second", result);
        }

        [Fact]
        public void ExecuteWithFallbacks_AllFail_ReturnsDefault()
        {
            // Arrange - Test line 136: all fail returns default
            // Proof: grep -n "return default!" src/Services/SafeOperationExecutor.cs
            // Line 136: return default!;
            // Act
            string result = SafeOperationExecutor.ExecuteWithFallbacks<string>(
                () => throw new Exception("1"),
                () => throw new Exception("2"),
                () => throw new Exception("3"));

            // Assert
            Assert.Null(result); // default(string) is null
        }

        [Fact]
        public void ExecuteWithFallbacks_NoOperations_ReturnsDefault()
        {
            // Arrange - Test empty operations array
            // Act
            int result = SafeOperationExecutor.ExecuteWithFallbacks<int>();

            // Assert
            Assert.Equal(0, result); // default(int)
        }

        #endregion
    }
}
