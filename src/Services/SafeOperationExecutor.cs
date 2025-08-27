using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services
{
    /// <summary>
    /// Utility class for executing operations safely with common defensive patterns
    /// Prevents cascading failures and provides consistent error handling
    /// UNIVERSAL: All plugins need defensive operation patterns
    /// </summary>
    public static class SafeOperationExecutor
    {
        /// <summary>
        /// Executes file operation with proper file locking and retry logic
        /// </summary>
        public static T ExecuteFileOperation<T>(
            string filePath, 
            Func<string, T> operation, 
            T fallbackValue = default,
            int maxRetries = 3)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return fallbackValue;
            }

            Exception lastException = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Defensive: Ensure file exists before operation
                    if (!File.Exists(filePath))
                    {
                        return fallbackValue;
                    }

                    return operation(filePath);
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    lastException = ex;
                    
                    // Wait briefly before retry (file might be locked)
                    Thread.Sleep(500 * attempt);
                }
                catch (UnauthorizedAccessException)
                {
                    return fallbackValue;
                }
                catch (Exception)
                {
                    return fallbackValue;
                }
            }

            return fallbackValue;
        }

        /// <summary>
        /// Executes async operation with timeout and defensive error handling
        /// </summary>
        public static async Task<T> ExecuteWithTimeoutAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            TimeSpan timeout,
            T fallbackValue = default,
            string operationName = "Operation")
        {
            using var cts = new CancellationTokenSource(timeout);
            
            try
            {
                return await operation(cts.Token);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                // Timeout occurred
                return fallbackValue;
            }
            catch (Exception)
            {
                return fallbackValue;
            }
        }

        /// <summary>
        /// Executes operation with exception containment
        /// Returns success status and result via out parameter
        /// </summary>
        public static bool TryExecute<T>(Func<T> operation, out T result, T fallbackValue = default)
        {
            try
            {
                result = operation();
                return true;
            }
            catch
            {
                result = fallbackValue;
                return false;
            }
        }

        /// <summary>
        /// Executes async operation with exception containment
        /// Returns success status and result via tuple
        /// </summary>
        public static async Task<(bool Success, T Result)> TryExecuteAsync<T>(
            Func<Task<T>> operation, 
            T fallbackValue = default)
        {
            try
            {
                var result = await operation();
                return (true, result);
            }
            catch
            {
                return (false, fallbackValue);
            }
        }

        /// <summary>
        /// Executes operation with multiple fallback strategies
        /// </summary>
        public static T ExecuteWithFallbacks<T>(params Func<T>[] operations)
        {
            foreach (var operation in operations)
            {
                try
                {
                    return operation();
                }
                catch
                {
                    // Continue to next fallback
                }
            }
            
            return default(T);
        }
    }
}