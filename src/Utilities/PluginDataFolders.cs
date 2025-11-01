using System;
using System.IO;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Computes per-plugin data folder roots for file-backed features (cache, validators, etc.).
    /// Because Lidarr.Plugin.Common is loaded side-by-side into each plugin's AssemblyLoadContext,
    /// deriving the fingerprint from this assembly's location yields natural isolation between plugins.
    /// </summary>
    internal static class PluginDataFolders
    {
        private static readonly string Fingerprint;

        static PluginDataFolders()
        {
            try
            {
                var loc = typeof(PluginDataFolders).Assembly.Location;
                var baseDir = string.IsNullOrEmpty(loc) ? AppContext.BaseDirectory : (Path.GetDirectoryName(loc) ?? AppContext.BaseDirectory);
                // Hash the absolute directory path to avoid leaking PII; keep it short for path length safety.
                var hash = HashingUtility.ComputeSHA256(baseDir);
                Fingerprint = hash.Substring(0, Math.Min(32, hash.Length));
            }
            catch
            {
                // Best effort fallback – still segregate by process when hashing fails.
                Fingerprint = Environment.ProcessId.ToString();
            }
        }

        public static string For(string category)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArrPlugins", category, Fingerprint);
            Directory.CreateDirectory(root);
            return root;
        }
    }
}

