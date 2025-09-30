using System;
using System.IO;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class FileSystemUtility
    {
        public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentNullException(nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));

            File.Move(sourcePath, destinationPath, overwrite);
        }
    }
}
