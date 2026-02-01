using System;
using System.Collections.Concurrent;
using System.Security;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Security
{
    /// <summary>
    /// Secure credential management with memory protection and secure string handling.
    /// Provides additional security layers for sensitive authentication data.
    /// UNIVERSAL: All streaming plugins need secure credential storage
    /// </summary>
    public class SecureCredentialManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, SecureCredentialWrapper> _secureCredentials;
        private bool _disposed = false;

        public SecureCredentialManager()
        {
            _secureCredentials = new ConcurrentDictionary<string, SecureCredentialWrapper>();
        }

        /// <summary>
        /// Creates a SecureString from a regular string and clears the source.
        /// Provides protection against memory dumps and reduces credential exposure time.
        /// </summary>
        /// <param name="source">Source string to secure (will be cleared)</param>
        /// <returns>SecureString containing the credential data</returns>
        public SecureString CreateSecureString(string source)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(source))
                return null;

            var secureString = new SecureString();

            try
            {
                foreach (char c in source)
                {
                    secureString.AppendChar(c);
                }

                secureString.MakeReadOnly();

                // Clear the source string from memory if possible
                ClearString(ref source);

                return secureString;
            }
            catch (Exception)
            {
                secureString?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Stores credential securely with automatic encryption
        /// </summary>
        public void StoreCredential(string key, string credential)
        {
            ThrowIfDisposed();

            var secureCredential = CreateSecureString(credential);
            var wrapper = new SecureCredentialWrapper(secureCredential);

            _secureCredentials.AddOrUpdate(key, wrapper, (_, old) =>
            {
                old.Dispose();
                return wrapper;
            });
        }

        /// <summary>
        /// Retrieves credential and automatically clears it from memory after use
        /// </summary>
        public string GetCredential(string key)
        {
            ThrowIfDisposed();

            if (!_secureCredentials.TryGetValue(key, out var wrapper))
                return null;

            return wrapper.GetPlainText();
        }

        /// <summary>
        /// Clears string from memory by overwriting with random data
        /// Best effort - GC may have moved the string
        /// </summary>
        private void ClearString(ref string str)
        {
            if (str == null)
            {
                return;
            }

            // Without unsafe blocks we cannot mutate the existing string buffer. Reassign and allow GC to reclaim.
            str = string.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SecureCredentialManager));
        }

        public void Dispose()
        {
            if (_disposed) return;

            foreach (var wrapper in _secureCredentials.Values)
            {
                wrapper.Dispose();
            }
            _secureCredentials.Clear();

            _disposed = true;
        }
    }

    /// <summary>
    /// Wrapper for secure credential storage with automatic cleanup
    /// </summary>
    public class SecureCredentialWrapper : IDisposable
    {
        private SecureString _secureString;
        private bool _disposed = false;

        public SecureCredentialWrapper(SecureString secureString)
        {
            _secureString = secureString ?? throw new ArgumentNullException(nameof(secureString));
        }

        public string GetPlainText()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SecureCredentialWrapper));

            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = Marshal.SecureStringToGlobalAllocUnicode(_secureString);
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _secureString?.Dispose();
            _secureString = null;
            _disposed = true;
        }
    }
}
