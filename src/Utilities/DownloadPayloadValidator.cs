using System;
using System.IO;
using System.Text;

namespace Lidarr.Plugin.Common.Utilities
{
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
