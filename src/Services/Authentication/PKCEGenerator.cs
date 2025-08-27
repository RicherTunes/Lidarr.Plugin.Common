using System;
using System.Security.Cryptography;
using System.Text;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Interface for PKCE (Proof Key for Code Exchange) generation
    /// Used for secure OAuth 2.0 authentication flows
    /// </summary>
    public interface IPKCEGenerator
    {
        /// <summary>
        /// Generates a PKCE code verifier and challenge pair
        /// </summary>
        /// <param name="length">Length of the code verifier (43-128 characters, default 128)</param>
        /// <returns>Tuple containing the code verifier and S256 code challenge</returns>
        (string codeVerifier, string codeChallenge) GeneratePair(int length = 128);
        
        /// <summary>
        /// Creates an S256 code challenge from a code verifier
        /// </summary>
        /// <param name="codeVerifier">The code verifier to create a challenge for</param>
        /// <returns>The S256 code challenge</returns>
        string CreateS256Challenge(string codeVerifier);
    }

    /// <summary>
    /// Implementation of PKCE (Proof Key for Code Exchange) generator for OAuth 2.0
    /// Follows RFC 7636 specification for secure authorization code flow
    /// </summary>
    /// <remarks>
    /// PKCE prevents authorization code interception attacks by:
    /// 1. Client generates a random code_verifier
    /// 2. Client sends S256(code_verifier) as code_challenge with auth request
    /// 3. Client sends code_verifier when exchanging auth code for tokens
    /// 4. Server validates S256(code_verifier) matches original code_challenge
    /// 
    /// Used by streaming services like Tidal, Spotify, Apple Music for OAuth flows
    /// </remarks>
    public class PKCEGenerator : IPKCEGenerator
    {
        private const int MinimumLength = 43;
        private const int MaximumLength = 128;
        private const int DefaultLength = 128;
        
        // RFC 7636 unreserved characters: [A-Z] / [a-z] / [0-9] / "-" / "." / "_" / "~"
        private const string UnreservedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

        /// <summary>
        /// Generates a PKCE code verifier and challenge pair
        /// </summary>
        /// <param name="length">Length of the code verifier (43-128 characters as per RFC 7636)</param>
        /// <returns>Tuple containing the code verifier and S256 code challenge</returns>
        /// <exception cref="ArgumentOutOfRangeException">If length is not between 43 and 128</exception>
        public (string codeVerifier, string codeChallenge) GeneratePair(int length = DefaultLength)
        {
            if (length < MinimumLength || length > MaximumLength)
            {
                throw new ArgumentOutOfRangeException(nameof(length), 
                    $"Code verifier length must be between {MinimumLength} and {MaximumLength} characters (RFC 7636)");
            }

            var codeVerifier = GenerateCodeVerifier(length);
            var codeChallenge = CreateS256Challenge(codeVerifier);
            
            return (codeVerifier, codeChallenge);
        }

        /// <summary>
        /// Creates an S256 code challenge from a code verifier
        /// </summary>
        /// <param name="codeVerifier">The code verifier to create a challenge for</param>
        /// <returns>The base64url-encoded SHA256 hash of the code verifier</returns>
        /// <exception cref="ArgumentNullException">If codeVerifier is null or empty</exception>
        public string CreateS256Challenge(string codeVerifier)
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                throw new ArgumentNullException(nameof(codeVerifier), "Code verifier cannot be null or empty");
            }

            using var sha256 = SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            return Base64UrlEncode(challengeBytes);
        }

        /// <summary>
        /// Generates a cryptographically random code verifier
        /// </summary>
        /// <param name="length">Length of the code verifier</param>
        /// <returns>A random string of unreserved characters</returns>
        private string GenerateCodeVerifier(int length)
        {
            var result = new char[length];
            
            using var rng = RandomNumberGenerator.Create();
            var randomBytes = new byte[length];
            rng.GetBytes(randomBytes);
            
            for (int i = 0; i < length; i++)
            {
                // Use modulo to map random byte to character index
                // This is safe as we're using cryptographically secure random
                result[i] = UnreservedCharacters[randomBytes[i] % UnreservedCharacters.Length];
            }
            
            return new string(result);
        }

        /// <summary>
        /// Base64url encodes the input bytes (RFC 4648 Section 5)
        /// </summary>
        /// <param name="input">Bytes to encode</param>
        /// <returns>Base64url encoded string without padding</returns>
        private static string Base64UrlEncode(byte[] input)
        {
            // Convert to base64
            var base64 = Convert.ToBase64String(input);
            
            // Make URL-safe by replacing characters and removing padding
            return base64
                .Replace('+', '-')  // 62nd character
                .Replace('/', '_')  // 63rd character  
                .TrimEnd('=');      // Remove padding
        }

        /// <summary>
        /// Validates a code verifier against RFC 7636 requirements
        /// </summary>
        /// <param name="codeVerifier">The code verifier to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool IsValidCodeVerifier(string codeVerifier)
        {
            if (string.IsNullOrEmpty(codeVerifier))
                return false;
                
            if (codeVerifier.Length < MinimumLength || codeVerifier.Length > MaximumLength)
                return false;
                
            // Check all characters are unreserved
            foreach (char c in codeVerifier)
            {
                if (!UnreservedCharacters.Contains(c))
                    return false;
            }
            
            return true;
        }
    }
}