// <copyright file="LogRedactorProperties.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Lidarr.Plugin.Common.Observability;

namespace Lidarr.Plugin.Common.Tests.Properties
{
    /// <summary>
    /// FsCheck property tests for <see cref="LogRedactor"/>.
    /// Wave 27: complement to wave 25's failed Stryker mutation testing.
    /// </summary>
    public class LogRedactorProperties
    {
        /// <summary>
        /// <c>Redact(Redact(s)) == Redact(s)</c> for any string. Critical for idempotency
        /// across multiple log pipelines that may each invoke the redactor.
        /// </summary>
        [Property]
        public bool Redact_IsIdempotent(NonNull<string> input)
        {
            var once = LogRedactor.Redact(input.Get);
            var twice = LogRedactor.Redact(once);
            return once == twice;
        }

        /// <summary>
        /// Null and empty inputs round-trip to non-null empty strings. Tests the
        /// short-circuit branch in <see cref="LogRedactor.Redact"/>.
        /// </summary>
        [Property]
        public bool Redact_NullOrEmpty_ReturnsEmpty(bool useNull)
        {
            var input = useNull ? null : string.Empty;
            var result = LogRedactor.Redact(input);
            return result == string.Empty;
        }

        /// <summary>
        /// For strings built from short alphanumeric tokens (length less than 32) joined by
        /// spaces, no redaction should occur. The generic catch-all only triggers at length
        /// greater than or equal to 32 chars and structured prefixes (sk-, AIza, Bearer) need
        /// non-alnum characters that we exclude here.
        /// </summary>
        /// <param name="counts">Drives a list of small token lengths.</param>
        [Property]
        public bool Redact_ShortAlnumTokens_Unchanged(byte[] counts)
        {
            // Build the input deterministically from the random bytes so every iteration
            // produces a qualifying input (no Prop.When filter exhaustion).
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new System.Text.StringBuilder();
            foreach (var b in counts)
            {
                if (sb.Length > 0) sb.Append(' ');
                // length in [1, 31]
                var len = (b % 31) + 1;
                for (var i = 0; i < len; i++)
                {
                    sb.Append(alphabet[(b + i) % alphabet.Length]);
                }
            }
            var s = sb.ToString();
            return LogRedactor.Redact(s) == s;
        }

        /// <summary>
        /// Embedding a known long secret somewhere in arbitrary text guarantees the secret
        /// is no longer literally present in the output.
        /// </summary>
        [Property]
        public bool Redact_KnownSecret_RemovesLiteral(NonNull<string> prefix, NonNull<string> suffix)
        {
            // sk-ant-... is recognized by the Anthropic key pattern.
            const string secret = "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var input = prefix.Get + " " + secret + " " + suffix.Get;
            var redacted = LogRedactor.Redact(input);
            return !redacted.Contains(secret);
        }
    }
}
