using System.Collections.Generic;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Tests for E2E error classification patterns.
    /// These tests ensure the expected error codes are returned for known error messages.
    ///
    /// NOTE: These tests mirror the classification logic in scripts/lib/e2e-error-classifier.psm1.
    /// The goal is to have gates emit explicit errorCode values, reducing reliance on regex heuristics.
    /// These tests document the expected behavior and catch regressions.
    /// </summary>
    public class E2EErrorClassifierTests
    {
        // Mirror the patterns from e2e-error-classifier.psm1
        private static readonly Dictionary<string, string[]> ErrorCodePatterns = new()
        {
            ["E2E_AUTH_MISSING"] = new[]
            {
                "credentials not configured",
                "missing env vars",
                "missing/invalid credentials",
                "not authenticated",
                "auth error",
                "invalid_grant",
                "invalid_client",
                "unauthorized",
                "forbidden",
                "401",
                "403",
                "credential file missing",
                "oauth",
                "token",
                "apikey",
                "credential"
            },
            ["E2E_API_TIMEOUT"] = new[]
            {
                "timeout",
                "timed out",
                "connection refused",
                "unreachable"
            },
            ["E2E_NO_RELEASES_ATTRIBUTED"] = new[]
            {
                "no releases",
                "zero releases",
                "releases attributed"
            },
            ["E2E_QUEUE_NOT_FOUND"] = new[]
            {
                "queue",
                "not found in queue",
                "download queue"
            },
            ["E2E_ZERO_AUDIO_FILES"] = new[]
            {
                "audio files",
                "no audio",
                "zero files"
            },
            ["E2E_METADATA_MISSING"] = new[]
            {
                "metadata",
                "missing field",
                "required field"
            },
            ["E2E_DOCKER_UNAVAILABLE"] = new[]
            {
                "docker",
                "container",
                "daemon"
            },
            ["E2E_CONFIG_INVALID"] = new[]
            {
                "config",
                "configuration",
                "invalid setting"
            },
            ["E2E_IMPORT_FAILED"] = new[]
            {
                "import",
                "failed import"
            }
        };

        public static IEnumerable<object[]> AuthMissingMessages => new[]
        {
            new object[] { "Indexer credentials not configured - missing env vars QOBUZARR_AUTH_TOKEN" },
            new object[] { "401 Unauthorized" },
            new object[] { "403 Forbidden - invalid credentials" },
            new object[] { "OAuth token expired" },
            new object[] { "apikey missing from request" },
            new object[] { "Not authenticated - please provide credentials" },
            new object[] { "invalid_grant: The provided authorization grant is invalid" },
        };

        public static IEnumerable<object[]> ApiTimeoutMessages => new[]
        {
            new object[] { "Connection timed out after 30s" },
            new object[] { "Request timeout - server unreachable" },
            new object[] { "Connection refused to localhost:8686" },
        };

        public static IEnumerable<object[]> NoReleasesMessages => new[]
        {
            new object[] { "No releases found for artist" },
            new object[] { "Zero releases attributed to search" },
            new object[] { "Search returned no releases" },
        };

        [Theory]
        [MemberData(nameof(AuthMissingMessages))]
        public void AuthMissingPattern_MatchesExpectedMessages(string message)
        {
            var code = ClassifyErrorMessage(message);
            Assert.Equal("E2E_AUTH_MISSING", code);
        }

        [Theory]
        [MemberData(nameof(ApiTimeoutMessages))]
        public void ApiTimeoutPattern_MatchesExpectedMessages(string message)
        {
            var code = ClassifyErrorMessage(message);
            Assert.Equal("E2E_API_TIMEOUT", code);
        }

        [Theory]
        [MemberData(nameof(NoReleasesMessages))]
        public void NoReleasesPattern_MatchesExpectedMessages(string message)
        {
            var code = ClassifyErrorMessage(message);
            Assert.Equal("E2E_NO_RELEASES_ATTRIBUTED", code);
        }

        [Theory]
        [InlineData("Everything is fine", null)]
        [InlineData("Test completed successfully", null)]
        [InlineData("Gate passed with no issues", null)]
        public void SuccessMessages_DoNotMatchAnyPattern(string message, string? expectedCode)
        {
            var code = ClassifyErrorMessage(message);
            Assert.Equal(expectedCode, code);
        }

        [Fact]
        public void CredentialPrereq_IsSubsetOfAuthMissing()
        {
            // These patterns should all be credential prerequisites
            var credentialMessages = new[]
            {
                "credentials not configured",
                "missing env vars",
                "missing/invalid credentials",
                "not authenticated",
                "auth error",
                "invalid_grant",
                "invalid_client",
                "unauthorized",
                "forbidden",
                "401 error",
                "403 forbidden",
                "credential file missing"
            };

            foreach (var message in credentialMessages)
            {
                var code = ClassifyErrorMessage(message);
                Assert.Equal("E2E_AUTH_MISSING", code);
                Assert.True(IsCredentialPrereq(message), $"'{message}' should be a credential prerequisite");
            }
        }

        /// <summary>
        /// Simplified C# implementation of the classification logic.
        /// This mirrors Get-E2EErrorClassification in e2e-error-classifier.psm1.
        /// </summary>
        private static string? ClassifyErrorMessage(string message)
        {
            var normalized = message.ToLowerInvariant();

            foreach (var (code, patterns) in ErrorCodePatterns)
            {
                foreach (var pattern in patterns)
                {
                    if (normalized.Contains(pattern))
                    {
                        return code;
                    }
                }
            }

            return null;
        }

        private static readonly string[] CredentialPrereqPatterns = new[]
        {
            "credentials not configured",
            "missing env vars",
            "missing/invalid credentials",
            "not authenticated",
            "auth error",
            "invalid_grant",
            "invalid_client",
            "unauthorized",
            "forbidden",
            "401",
            "403",
            "credential file missing"
        };

        private static bool IsCredentialPrereq(string message)
        {
            var normalized = message.ToLowerInvariant();
            foreach (var pattern in CredentialPrereqPatterns)
            {
                if (normalized.Contains(pattern))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
