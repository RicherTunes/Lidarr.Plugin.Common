using System;
using System.IO;
using System.Text;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Coarse classification produced by <see cref="DownloadPayloadValidator.ClassifyPayload"/>.
    /// </summary>
    public enum PayloadKind
    {
        /// <summary>The sample is empty.</summary>
        Empty = 0,

        /// <summary>The sample matches a known audio container/codec signature.</summary>
        Audio = 1,

        /// <summary>The sample looks like an HTML document (likely an error page).</summary>
        Html = 2,

        /// <summary>The sample looks like a JSON document (likely an error / problem document).</summary>
        Json = 3,

        /// <summary>The sample looks like an XML document.</summary>
        Xml = 4,

        /// <summary>The sample looks like text but does not match a more specific kind.</summary>
        UnknownText = 5,

        /// <summary>The sample does not match any recognized signature.</summary>
        Unknown = 6,
    }

    /// <summary>
    /// Validates download payloads to ensure they contain valid audio content.
    /// Combines text/HTML detection with audio magic bytes validation.
    /// </summary>
    public static class DownloadPayloadValidator
    {
        // Audio format magic bytes
        private static readonly byte[] FlacMagic = Encoding.ASCII.GetBytes("fLaC");
        private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS");
        private static readonly byte[] RiffMagic = Encoding.ASCII.GetBytes("RIFF");
        private static readonly byte[] Id3Magic = Encoding.ASCII.GetBytes("ID3");
        private static readonly byte[] FtypMagic = Encoding.ASCII.GetBytes("ftyp");

        /// <summary>
        /// Validates a byte sample to ensure it looks like audio content.
        /// Throws InvalidDataException if validation fails.
        /// </summary>
        /// <param name="sample">First bytes of the download payload</param>
        /// <param name="fileExtension">Expected file extension (optional, e.g., "flac", ".mp3")</param>
        /// <param name="mimeType">Content-Type from response headers (optional)</param>
        public static void ValidateOrThrow(ReadOnlySpan<byte> sample, string? fileExtension = null, string? mimeType = null)
        {
            var ext = NormalizeExtension(fileExtension);
            if (sample.IsEmpty)
            {
                if (ext is "m4a" or "mp4")
                {
                    const int minimumMp4HeaderLength = 8;
                    throw new InvalidDataException(
                        $"Insufficient header bytes for MP4/M4A signature (requires {minimumMp4HeaderLength} bytes, got {sample.Length}).");
                }

                throw new InvalidDataException("Downloaded stream contained no data.");
            }

            if (ext is "m4a" or "mp4")
            {
                // MP4/M4A signature validation requires the "ftyp" box at offset 4.
                // If we don't have enough bytes, fail with a specific message to make diagnosis clear.
                const int minimumMp4HeaderLength = 8;
                if (sample.Length < minimumMp4HeaderLength)
                {
                    throw new InvalidDataException(
                        $"Insufficient header bytes for MP4/M4A signature (requires {minimumMp4HeaderLength} bytes, got {sample.Length}).");
                }
            }

            if (LooksLikeTextPayload(sample))
            {
                throw new InvalidDataException("Download returned non-audio content (HTML/JSON).");
            }

            if (!LooksLikeAudioPayload(sample, ext, mimeType))
            {
                throw new InvalidDataException("Download returned content that does not look like audio.");
            }
        }

        /// <summary>
        /// Validates a file on disk to ensure it contains valid audio content.
        /// Throws InvalidOperationException if validation fails.
        /// </summary>
        /// <param name="filePath">Path to the file to validate</param>
        public static void ValidateFileOrThrow(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("File not found for validation.", filePath);
            }

            Span<byte> magicBytes = stackalloc byte[12];
            int read;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                read = fs.Read(magicBytes);
                if (read < 4)
                {
                    throw new InvalidOperationException($"File too small for magic validation: {Path.GetFileName(filePath)}");
                }
            }

            var sample = magicBytes.Slice(0, read);
            var ext = Path.GetExtension(filePath);

            if (LooksLikeTextPayload(sample))
            {
                var hex = BitConverter.ToString(sample.ToArray());
                var ascii = ToAsciiPreview(sample);
                throw new InvalidOperationException($"File contains non-audio content '{hex}' ('{ascii}') for {Path.GetFileName(filePath)}");
            }

            if (!LooksLikeAudioPayload(sample, NormalizeExtension(ext), null))
            {
                var hex = BitConverter.ToString(sample.ToArray());
                var ascii = ToAsciiPreview(sample);
                throw new InvalidOperationException($"Invalid audio magic bytes '{hex}' ('{ascii}') for {Path.GetFileName(filePath)}");
            }
        }

        /// <summary>
        /// Classifies the payload kind to help callers produce diagnostic messages
        /// before the bytes land on disk. Distinguishes between empty, audio, HTML,
        /// JSON, XML, and unknown text payloads.
        /// </summary>
        /// <param name="sample">First bytes of the payload.</param>
        /// <param name="fileExtension">Optional file extension hint (e.g., "flac", ".mp3").</param>
        /// <param name="mimeType">Optional Content-Type header.</param>
        /// <returns>A <see cref="PayloadKind"/> value indicating what the bytes look like.</returns>
        public static PayloadKind ClassifyPayload(
            ReadOnlySpan<byte> sample,
            string? fileExtension = null,
            string? mimeType = null)
        {
            if (sample.IsEmpty)
            {
                return PayloadKind.Empty;
            }

            if (LooksLikeHtmlPayload(sample))
            {
                return PayloadKind.Html;
            }

            if (LooksLikeJsonPayload(sample))
            {
                return PayloadKind.Json;
            }

            if (LooksLikeXmlPayload(sample))
            {
                return PayloadKind.Xml;
            }

            if (LooksLikeAudioPayload(sample, NormalizeExtension(fileExtension), mimeType))
            {
                return PayloadKind.Audio;
            }

            // Fallback to legacy general text detector for fuzzier text signals
            // (catches HTML markup further into the buffer, etc.)
            if (LooksLikeTextPayload(sample))
            {
                return PayloadKind.UnknownText;
            }

            return PayloadKind.Unknown;
        }

        /// <summary>
        /// Returns true when the payload looks like an HTML document (DOCTYPE, &lt;html, &lt;script).
        /// </summary>
        public static bool LooksLikeHtmlPayload(ReadOnlySpan<byte> sample)
        {
            if (!TryGetFirstNonWhitespace(sample, out var index, out var first))
            {
                return false;
            }

            if (first == (byte)'<')
            {
                var max = Math.Min(sample.Length, 256);
                var text = Encoding.UTF8.GetString(sample.Slice(index, max - index));
                return text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                       || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                       || text.StartsWith("<script", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Returns true when the payload looks like a JSON document or RFC 7807 problem document.
        /// </summary>
        public static bool LooksLikeJsonPayload(ReadOnlySpan<byte> sample)
        {
            if (!TryGetFirstNonWhitespace(sample, out var index, out var first))
            {
                return false;
            }

            if (first == (byte)'{')
            {
                int j = index + 1;
                while (j < sample.Length && sample[j] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    j++;
                }

                return j < sample.Length && (sample[j] == (byte)'"' || sample[j] == (byte)'}');
            }

            if (first == (byte)'[')
            {
                int j = index + 1;
                while (j < sample.Length && sample[j] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    j++;
                }

                if (j >= sample.Length)
                {
                    return false;
                }

                return sample[j] is (byte)'{' or (byte)'"' or (byte)']';
            }

            return false;
        }

        /// <summary>
        /// Returns true when the payload looks like an XML document (&lt;?xml or root element).
        /// </summary>
        public static bool LooksLikeXmlPayload(ReadOnlySpan<byte> sample)
        {
            if (!TryGetFirstNonWhitespace(sample, out var index, out var first))
            {
                return false;
            }

            if (first != (byte)'<')
            {
                return false;
            }

            var max = Math.Min(sample.Length, 256);
            var text = Encoding.UTF8.GetString(sample.Slice(index, max - index));
            return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetFirstNonWhitespace(ReadOnlySpan<byte> sample, out int index, out byte first)
        {
            index = 0;
            first = 0;

            if (sample.IsEmpty)
            {
                return false;
            }

            // Skip UTF-8 BOM
            if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                index = 3;
            }

            while (index < sample.Length && sample[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                index++;
            }

            if (index >= sample.Length)
            {
                return false;
            }

            first = sample[index];
            return true;
        }

        /// <summary>
        /// Checks if the payload looks like text content (HTML, JSON, XML).
        /// </summary>
        public static bool LooksLikeTextPayload(ReadOnlySpan<byte> sample)
        {
            if (sample.IsEmpty)
            {
                return false;
            }

            // Fast-path: skip UTF-8 BOM (EF BB BF) and leading whitespace
            int index = 0;
            if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                index = 3;
            }

            while (index < sample.Length && sample[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                index++;
            }

            if (index >= sample.Length)
            {
                return false;
            }

            var first = sample[index];
            var max = Math.Min(sample.Length, 256);

            // Heuristic: detect likely HTML/XML/JSON based on stronger evidence than the first byte alone.
            if (first == (byte)'<')
            {
                var text = Encoding.UTF8.GetString(sample.Slice(index, max - index));
                return text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                       || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                       || text.StartsWith("<script", StringComparison.OrdinalIgnoreCase)
                       || text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
            }

            if (first == (byte)'{')
            {
                // JSON object: { "key": ... } or {"key": ...}
                int j = index + 1;
                while (j < sample.Length && sample[j] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    j++;
                }

                return j < sample.Length && sample[j] == (byte)'"';
            }

            if (first == (byte)'[')
            {
                // JSON array: [ { ... } ] or [ "..." ] or []
                int j = index + 1;
                while (j < sample.Length && sample[j] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    j++;
                }

                if (j >= sample.Length)
                {
                    return false;
                }

                return sample[j] is (byte)'{' or (byte)'"' or (byte)']';
            }

            // Fallback: look for common HTML markers even if not first char
            var fallbackText = Encoding.UTF8.GetString(sample.Slice(0, max));
            return fallbackText.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
                   || fallbackText.Contains("<html", StringComparison.OrdinalIgnoreCase)
                   || fallbackText.Contains("<script", StringComparison.OrdinalIgnoreCase)
                   || fallbackText.Contains("<?xml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the payload looks like valid audio content.
        /// </summary>
        public static bool LooksLikeAudioPayload(ReadOnlySpan<byte> sample, string? normalizedExtension = null, string? mimeType = null)
        {
            if (sample.Length < 2)
            {
                return false;
            }

            // Check for known audio signatures
            var hasFlac = HasMagic(sample, FlacMagic, 0);
            var hasOgg = HasMagic(sample, OggMagic, 0);
            var hasRiff = HasMagic(sample, RiffMagic, 0);
            var hasId3 = HasMagic(sample, Id3Magic, 0);
            var hasMpegSync = sample[0] == 0xFF && (sample[1] & 0xE0) == 0xE0;
            var hasFtyp = HasMagic(sample, FtypMagic, 4); // ftyp appears at offset 4 in MP4/M4A

            var hasAnyAudioSignature = hasFlac || hasOgg || hasRiff || hasId3 || hasMpegSync || hasFtyp;

            if (string.IsNullOrEmpty(normalizedExtension))
            {
                return hasAnyAudioSignature;
            }

            // Extension-specific strictness
            return normalizedExtension switch
            {
                "flac" => hasFlac,
                "m4a" or "mp4" => hasFtyp,
                "mp3" => hasId3 || hasMpegSync,
                "ogg" or "oga" => hasOgg,
                "wav" => hasRiff,
                _ => hasAnyAudioSignature // Accept any recognized audio signature for unknown extensions
            };
        }

        /// <summary>
        /// Checks if the byte array contains valid audio magic bytes (for backward compatibility).
        /// </summary>
        public static bool IsValidAudioMagicBytes(ReadOnlySpan<byte> magicBytes)
        {
            return LooksLikeAudioPayload(magicBytes);
        }

        private static string NormalizeExtension(string? fileExtension)
        {
            if (string.IsNullOrWhiteSpace(fileExtension))
            {
                return string.Empty;
            }

            return fileExtension.Trim().TrimStart('.').ToLowerInvariant();
        }

        private static bool HasMagic(ReadOnlySpan<byte> sample, byte[] magic, int offset)
        {
            if (offset < 0 || sample.Length < offset + magic.Length)
            {
                return false;
            }

            return sample.Slice(offset, magic.Length).SequenceEqual(magic);
        }

        private static string ToAsciiPreview(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                sb.Append(b is >= 32 and <= 126 ? (char)b : '.');
            }
            return sb.ToString();
        }
    }
}
