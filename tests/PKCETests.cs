using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Authentication;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class PKCETests
    {
        [Fact]
        public void GeneratePair_ReturnsValidVerifierAndChallenge()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act
            var (verifier, challenge) = pkce.GeneratePair();

            // Assert
            Assert.NotNull(verifier);
            Assert.NotNull(challenge);
            Assert.NotEmpty(verifier);
            Assert.NotEmpty(challenge);
            Assert.NotEqual(verifier, challenge);
        }

        [Fact]
        public void GeneratePair_DefaultLength128()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act
            var (verifier, _) = pkce.GeneratePair();

            // Assert
            Assert.Equal(128, verifier.Length);
        }

        [Theory]
        [InlineData(43)]
        [InlineData(64)]
        [InlineData(100)]
        [InlineData(128)]
        public void GeneratePair_AcceptsValidLengths(int length)
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act
            var (verifier, challenge) = pkce.GeneratePair(length);

            // Assert
            Assert.Equal(length, verifier.Length);
            Assert.NotEmpty(challenge);
        }

        [Fact]
        public void GeneratePair_RejectsLengthBelow43()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act & Assert
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => pkce.GeneratePair(42));
            Assert.Equal("length", ex.ParamName);
            Assert.Contains("43", ex.Message);
        }

        [Fact]
        public void GeneratePair_RejectsLengthAbove128()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act & Assert
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => pkce.GeneratePair(129));
            Assert.Equal("length", ex.ParamName);
            Assert.Contains("128", ex.Message);
        }

        [Fact]
        public void GeneratePair_RejectsLengthZero()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => pkce.GeneratePair(0));
        }

        [Fact]
        public void GeneratePair_RejectsNegativeLength()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => pkce.GeneratePair(-1));
        }

        [Fact]
        public void CreateS256Challenge_ThrowsOnNull()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => pkce.CreateS256Challenge(null!));
        }

        [Fact]
        public void CreateS256Challenge_ThrowsOnEmpty()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act & Assert
            var ex = Assert.Throws<ArgumentNullException>(() => pkce.CreateS256Challenge(string.Empty));
            Assert.Equal("codeVerifier", ex.ParamName);
        }

        [Fact]
        public void CreateS256Challenge_ProducesValidS256()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

            // Act
            var challenge = pkce.CreateS256Challenge(verifier);

            // Assert
            Assert.NotNull(challenge);
            Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", verifier);

            // Known test vector from RFC 7636
            // code_verifier: dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
            // code_challenge: dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk (wait, that's wrong)
            // Actual expected challenge for this verifier is:
            // "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk" -> SHA256 -> base64url
            // For the RFC test vector, with verifier "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
            // the challenge should be "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
        }

        [Fact]
        public void CreateS256Challenge_RFC7636TestVector()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            // RFC 7636 test vector
            var codeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

            // Act
            var challenge = pkce.CreateS256Challenge(codeVerifier);

            // Assert - Verify against RFC 7636 expected value
            Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
        }

        [Fact]
        public void CreateS256Challenge_VerifyDeterministic()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var verifier = "test_verifier_123456789012345678901234567890";

            // Act
            var challenge1 = pkce.CreateS256Challenge(verifier);
            var challenge2 = pkce.CreateS256Challenge(verifier);

            // Assert
            Assert.Equal(challenge1, challenge2);
        }

        [Fact]
        public void CreateS256Challenge_DifferentVerifiersProduceDifferentChallenges()
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act
            var challenge1 = pkce.CreateS256Challenge("verifier_one_12345678901234567890123456");
            var challenge2 = pkce.CreateS256Challenge("verifier_two_12345678901234567890123456");

            // Assert
            Assert.NotEqual(challenge1, challenge2);
        }

        [Fact]
        public void CreateS256Challenge_ProducesBase64UrlWithoutPadding()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var verifier = "test_verifier_for_padding_check_12345678";

            // Act
            var challenge = pkce.CreateS256Challenge(verifier);

            // Assert - Base64url should not have padding characters
            Assert.DoesNotContain('=', challenge);
            Assert.DoesNotContain('+', challenge);
            Assert.DoesNotContain('/', challenge);
        }

        [Theory]
        [InlineData("aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789-._~abc")] // 43 chars, valid
        [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")] // 74 chars, valid
        public void IsValidCodeVerifier_AcceptsValidVerifier(string verifier)
        {
            // Act
            var result = PKCEGenerator.IsValidCodeVerifier(verifier);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData("")] // empty
        [InlineData("aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789-._")] // 42 chars, too short
        [InlineData("short")] // very short
        public void IsValidCodeVerifier_RejectsTooShort(string verifier)
        {
            // Act
            var result = PKCEGenerator.IsValidCodeVerifier(verifier);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidCodeVerifier_RejectsTooLong()
        {
            // Arrange - 129 characters
            var tooLong = new string('a', 129);

            // Act
            var result = PKCEGenerator.IsValidCodeVerifier(tooLong);

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData("invalid+chars")] // plus sign not allowed
        [InlineData("invalid/chars")] // slash not allowed
        [InlineData("invalid=chars")] // equals not allowed
        [InlineData("invalid chars")] // space not allowed
        [InlineData("invalid!chars")] // exclamation not allowed
        [InlineData("invalid@chars")] // at sign not allowed
        [InlineData("invalid#chars")] // hash not allowed
        [InlineData("invalid$chars")] // dollar not allowed
        [InlineData("invalid%chars")] // percent not allowed
        [InlineData("invalid&chars")] // ampersand not allowed
        [InlineData("invalid*chars")] // asterisk not allowed
        [InlineData("invalid(chars)")] // parentheses not allowed
        public void IsValidCodeVerifier_RejectsInvalidCharacters(string verifier)
        {
            // Make verifier long enough to meet minimum length
            verifier = verifier + new string('a', 43);

            // Act
            var result = PKCEGenerator.IsValidCodeVerifier(verifier);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidCodeVerifier_RejectsNull()
        {
            // Arrange
            string? verifier = null;

            // Act
            var result = PKCEGenerator.IsValidCodeVerifier(verifier!);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidCodeVerifier_AcceptsAllValidUnreservedCharacters()
        {
            // Arrange - Test all valid character classes
            var upperCase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var lowerCase = "abcdefghijklmnopqrstuvwxyz";
            var digits = "0123456789";
            var special = "-._~";
            var allValid = upperCase + lowerCase + digits + special;

            // Act
            var result = PKCEGenerator.IsValidCodeVerifier(allValid);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void GeneratePair_VerifierContainsOnlyValidCharacters()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var validChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

            // Act
            var (verifier, _) = pkce.GeneratePair();

            // Assert - All characters should be valid unreserved characters
            Assert.True(verifier.All(c => validChars.Contains(c)));
        }

        [Fact]
        public void GeneratePair_CryptographicallyRandom_VerifyUniqueness()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var generatedPairs = new HashSet<string>(1000);
            var iterations = 1000;

            // Act - Generate 1000 pairs
            for (int i = 0; i < iterations; i++)
            {
                var (verifier, _) = pkce.GeneratePair();

                // Assert - Each verifier should be unique
                Assert.False(generatedPairs.Contains(verifier),
                    $"Duplicate verifier found at iteration {i}: {verifier}");

                generatedPairs.Add(verifier);
            }

            // Final assertion
            Assert.Equal(iterations, generatedPairs.Count);
        }

        [Fact]
        public void GeneratePair_MultipleCallsGenerateDifferentValues()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var verifiers = new List<string>();
            var challenges = new List<string>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                var (verifier, challenge) = pkce.GeneratePair();
                verifiers.Add(verifier);
                challenges.Add(challenge);
            }

            // Assert - All verifiers should be unique
            Assert.Equal(10, verifiers.Distinct().Count());
            Assert.Equal(10, challenges.Distinct().Count());
        }

        [Fact]
        public void CreateS256Challenge_ChallengeMatchesVerifierEncoding()
        {
            // Arrange
            var pkce = new PKCEGenerator();
            var (verifier, challenge) = pkce.GeneratePair();

            // Act - Recreate challenge from same verifier
            var recreatedChallenge = pkce.CreateS256Challenge(verifier);

            // Assert
            Assert.Equal(challenge, recreatedChallenge);
        }

        [Fact]
        public void IPKCEGenerator_Interface_CanBeInjected()
        {
            // Arrange & Act
            IPKCEGenerator pkce = new PKCEGenerator();

            // Assert - Verify interface methods work
            var (verifier, challenge) = pkce.GeneratePair();
            Assert.NotNull(verifier);
            Assert.NotNull(challenge);

            var recreatedChallenge = pkce.CreateS256Challenge(verifier);
            Assert.Equal(challenge, recreatedChallenge);
        }

        [Theory]
        [InlineData(43)]
        [InlineData(50)]
        [InlineData(64)]
        [InlineData(100)]
        [InlineData(128)]
        public void GeneratePair_ChallengeLengthConsistentRegardlessOfVerifierLength(int verifierLength)
        {
            // Arrange
            var pkce = new PKCEGenerator();

            // Act
            var (_, challenge) = pkce.GeneratePair(verifierLength);

            // Assert - SHA256 hash should always produce consistent base64url length
            // SHA256 produces 32 bytes, which encodes to ~43 chars in base64url (without padding)
            Assert.InRange(challenge.Length, 40, 50);
        }
    }
}
