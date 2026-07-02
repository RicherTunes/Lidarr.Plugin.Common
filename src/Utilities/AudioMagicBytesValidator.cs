using System;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Validates audio files by checking their magic bytes (file signature).
    /// Compatibility facade over <see cref="DownloadPayloadValidator"/>.
    /// </summary>
    public static class AudioMagicBytesValidator
    {
        /// <summary>
        /// Validates that a file has valid audio magic bytes.
        /// Throws InvalidOperationException if validation fails.
        /// </summary>
        /// <param name="filePath">Path to the file to validate</param>
        /// <exception cref="ArgumentException">Thrown if filePath is null or empty</exception>
        /// <exception cref="InvalidOperationException">Thrown if file doesn't have valid audio magic bytes</exception>
        public static void ValidateAudioMagicBytes(string filePath)
        {
            DownloadPayloadValidator.ValidateFileOrThrow(filePath);
        }

        /// <summary>
        /// Checks if the given magic bytes represent a valid audio file format.
        /// Supports every signature recognized by <see cref="DownloadPayloadValidator"/>.
        /// </summary>
        /// <param name="magicBytes">First bytes of the file to check</param>
        /// <returns>True if the magic bytes match a known audio format</returns>
        public static bool IsValidAudioMagicBytes(ReadOnlySpan<byte> magicBytes)
        {
            return DownloadPayloadValidator.IsValidAudioMagicBytes(magicBytes);
        }
    }
}
