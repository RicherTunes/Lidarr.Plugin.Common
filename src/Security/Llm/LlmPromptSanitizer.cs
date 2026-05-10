using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Security.Llm
{
    /// <summary>
    /// Sanitizer for LLM prompts. Defends against prompt-injection / jailbreak
    /// attempts, control-character payload obfuscation, and unbounded resource
    /// usage. Cross-plugin core for any plugin shipping LLM-derived recommendations.
    /// </summary>
    /// <remarks>
    /// Implements defense-in-depth with multiple sanitization layers:
    /// <list type="number">
    ///   <item>Truncation to <see cref="MaxPromptLength"/></item>
    ///   <item>Control-character + zero-width / directional-override Unicode removal</item>
    ///   <item>Known-pattern injection removal (operates on the lower-cased copy)</item>
    ///   <item>Whitespace normalization</item>
    ///   <item>Sensitive-data redaction (API keys, embedded credentials, emails, password=value pairs)</item>
    ///   <item>Final injection-attempt detection — falls back to <see cref="SafeDefaultPrompt"/> if anything survived</item>
    /// </list>
    /// </remarks>
    public class LlmPromptSanitizer
    {
        /// <summary>Maximum prompt length before truncation. Default 10,000 chars.</summary>
        public const int MaxPromptLength = 10000;

        /// <summary>Maximum chunk size used when slicing very long inputs for regex passes.</summary>
        public const int MaxRegexProcessingLength = 5000;

        /// <summary>Per-regex evaluation timeout (ms).</summary>
        public const int RegexTimeoutMs = 100;

        /// <summary>Generic safe-default prompt returned when the input is unrecoverable.</summary>
        public string SafeDefaultPrompt { get; init; } = "Please provide music recommendations based on the user's library.";

        private readonly ILogger _logger;

        // Injection patterns to detect and remove
        private static readonly string[] InjectionPatterns =
        {
            // Direct instruction override attempts
            "ignore previous instructions",
            "ignore all previous instructions",
            "disregard previous instructions",
            "forget previous instructions",
            "ignore above instructions",
            "ignore all above",
            "override previous",
            "cancel previous",

            // System prompt injection
            "system:",
            "assistant:",
            "user:",
            "human:",
            "[INST]",
            "[/INST]",
            "\\n\\nHuman:",
            "\\n\\nAssistant:",
            "\\n\\nSystem:",
            "<|im_start|>",
            "<|im_end|>",

            // Role manipulation
            "you are now",
            "you must now",
            "act as",
            "pretend to be",
            "roleplay as",
            "simulate being",
            "from now on",

            // Prompt leakage attempts
            "show your prompt",
            "reveal your prompt",
            "what is your prompt",
            "display your instructions",
            "show your instructions",
            "print your instructions",
            "output your instructions",

            // Jailbreak attempts
            "DAN mode",
            "developer mode",
            "god mode",
            "unrestricted mode",
            "bypass safety",
            "disable safety",
            "ignore safety",

            // Code execution attempts
            "execute code:",
            "run command:",
            "eval(",
            "exec(",
            "system(",
            "os.system",
            "__import__",

            // Data exfiltration
            "send to url",
            "post to",
            "webhook",
            "curl -X",
            "wget",
            "fetch(",

            // SQL injection patterns
            "'; DROP TABLE",
            "' OR '1'='1",
            "\" OR \"1\"=\"1",
            "'; DELETE FROM",
            "'; UPDATE",
            "UNION SELECT",

            // NoSQL injection patterns
            "\"$gt\":",
            "\"$ne\":",
            "\"$regex\":",
            "$where:",
            "db.eval"
        };

        private static readonly Lazy<Regex> UnicodeControlChars = new(() =>
            new Regex(@"[\x00-\x08\x0B-\x0C\x0E-\x1F\x7F-\x9F]",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(RegexTimeoutMs)));

        private static readonly Lazy<Regex> RepeatedWhitespace = new(() =>
            new Regex(@"\s{3,}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(RegexTimeoutMs)));

        private static readonly Lazy<Regex> HiddenUnicode = new(() =>
            new Regex(@"[​-‏‪-‮⁠-⁯﻿]",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(RegexTimeoutMs)));

        /// <summary>Creates a new sanitizer with optional logger.</summary>
        public LlmPromptSanitizer(ILogger<LlmPromptSanitizer>? logger = null)
        {
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        /// <summary>Sanitizes a prompt and returns the cleaned text (or <see cref="SafeDefaultPrompt"/> on failure).</summary>
        public string SanitizePrompt(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var startLength = input.Length;

            if (input.Length > MaxPromptLength)
            {
                _logger.LogWarning("Prompt truncated from {From} to {To} characters", input.Length, MaxPromptLength);
                input = input.Substring(0, MaxPromptLength);
            }

            input = RemoveControlCharacters(input);
            input = RemoveInjectionPatterns(input);
            input = NormalizeWhitespace(input);
            input = RemoveSensitiveData(input);

            if (ContainsInjectionAttempt(input))
            {
                _logger.LogWarning("Injection attempt still detected after sanitization, returning safe default");
                return SafeDefaultPrompt;
            }

            if (input.Length < startLength * 0.5)
            {
                _logger.LogWarning("Sanitization removed >50% of content ({From} -> {To} chars), possible injection attempt",
                    startLength, input.Length);
            }

            return input.Trim();
        }

        /// <summary>Async wrapper around <see cref="SanitizePrompt"/>.</summary>
        public Task<string> SanitizePromptAsync(string? input, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => SanitizePrompt(input), cancellationToken);
        }

        /// <summary>Returns true if the input contains any known injection pattern, suspicious Unicode, or excessive special characters.</summary>
        public bool ContainsInjectionAttempt(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            var lowerInput = input.ToLowerInvariant();

            foreach (var pattern in InjectionPatterns)
            {
                if (lowerInput.Contains(pattern.ToLowerInvariant()))
                {
                    _logger.LogWarning("Injection pattern detected: {Pattern}",
                        pattern.Substring(0, Math.Min(pattern.Length, 20)));
                    return true;
                }
            }

            if (HasSuspiciousUnicode(input))
            {
                _logger.LogWarning("Suspicious Unicode sequences detected");
                return true;
            }

            if (HasExcessiveSpecialCharacters(input))
            {
                _logger.LogWarning("Excessive special characters detected");
                return true;
            }

            return false;
        }

        /// <summary>Redacts API keys, credentials, emails, and password=value pairs.</summary>
        public string RemoveSensitiveData(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return input ?? string.Empty;

            var result = input;

            result = Regex.Replace(result, @"\b[A-Za-z0-9]{32,}\b", "[REDACTED_KEY]",
                RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutMs));

            result = Regex.Replace(result, @"https?://[^:]+:[^@]+@[^\s]+", "[REDACTED_URL]",
                RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutMs));

            result = Regex.Replace(result, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", "[REDACTED_EMAIL]",
                RegexOptions.None, TimeSpan.FromMilliseconds(RegexTimeoutMs));

            result = Regex.Replace(result, @"(password|pwd|pass|token|key|secret|api_key|apikey)\s*[=:]\s*\S+",
                "$1=[REDACTED]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(RegexTimeoutMs));

            if (!string.Equals(result, input, StringComparison.Ordinal))
            {
                _logger.LogWarning("Sensitive data patterns were redacted from prompt");
            }

            return result;
        }

        // ----- Internals -----

        private string RemoveControlCharacters(string input)
        {
            try
            {
                input = UnicodeControlChars.Value.Replace(input, " ");
                input = HiddenUnicode.Value.Replace(input, "");
                return input;
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Regex timeout while removing control characters");
                return input;
            }
        }

        private string RemoveInjectionPatterns(string input)
        {
            if (input.Length > MaxRegexProcessingLength)
            {
                var chunks = new List<string>();
                for (int i = 0; i < input.Length; i += MaxRegexProcessingLength)
                {
                    var chunk = input.Substring(i, Math.Min(MaxRegexProcessingLength, input.Length - i));
                    chunks.Add(RemoveInjectionPatternsFromChunk(chunk));
                }
                return string.Join("", chunks);
            }

            return RemoveInjectionPatternsFromChunk(input);
        }

        private string RemoveInjectionPatternsFromChunk(string chunk)
        {
            var result = chunk;

            foreach (var pattern in InjectionPatterns)
            {
                var regex = new Regex(Regex.Escape(pattern),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(RegexTimeoutMs));

                try
                {
                    result = regex.Replace(result, " ");
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger.LogDebug("Timeout processing pattern: {Pattern}",
                        pattern.Substring(0, Math.Min(pattern.Length, 20)));
                }
            }

            return result;
        }

        private string NormalizeWhitespace(string input)
        {
            try
            {
                input = RepeatedWhitespace.Value.Replace(input, " ");

                var lines = input.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                return string.Join("\n", lines.Select(l => l.Trim()));
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Regex timeout while normalizing whitespace");
                return input;
            }
        }

        private static bool HasSuspiciousUnicode(string input)
        {
            // Unicode directional override characters
            if (input.Any(c => c >= 0x202A && c <= 0x202E))
                return true;

            // Excessive non-ASCII characters
            var nonAsciiCount = input.Count(c => c > 127);
            if (nonAsciiCount > input.Length * 0.5)
                return true;

            return HasMixedScripts(input);
        }

        private static bool HasMixedScripts(string input)
        {
            bool hasLatin = false;
            bool hasCyrillic = false;
            bool hasArabic = false;
            bool hasChinese = false;

            foreach (char c in input)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    hasLatin = true;
                else if (c >= 0x0400 && c <= 0x04FF)
                    hasCyrillic = true;
                else if (c >= 0x0600 && c <= 0x06FF)
                    hasArabic = true;
                else if (c >= 0x4E00 && c <= 0x9FFF)
                    hasChinese = true;
            }

            // Suspicious if 3+ scripts mixed (possible homograph attack)
            var scriptCount = new[] { hasLatin, hasCyrillic, hasArabic, hasChinese }.Count(x => x);
            return scriptCount > 2;
        }

        private static bool HasExcessiveSpecialCharacters(string input)
        {
            var specialCharCount = input.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            return specialCharCount > input.Length * 0.4;
        }
    }
}
