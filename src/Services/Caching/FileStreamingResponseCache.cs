using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// File-backed streaming response cache; persists CachedHttpResponse entries.
    /// Conservative by default: callers decide ShouldCache and duration; this class focuses on storage.
    /// </summary>
    public sealed class FileStreamingResponseCache : IStreamingResponseCache
    {
        private readonly string _root;
        private readonly TimeSpan _defaultDuration;
        private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly int _maxEntries;
        private readonly long _maxBytes;

        public FileStreamingResponseCache(string? folder = null, TimeSpan? defaultDuration = null)
        {
            _root = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArrPlugins", "resp-cache");
            Directory.CreateDirectory(_root);
            _defaultDuration = defaultDuration ?? TimeSpan.FromHours(6);
            _maxEntries = ReadIntEnv("ARR_RESP_CACHE_MAX_ENTRIES", 20000);
            _maxBytes = ReadLongEnv("ARR_RESP_CACHE_MAX_MB", 256) * 1024L * 1024L;
            TryCleanupExpired();
        }

        public T? Get<T>(string endpoint, Dictionary<string, string> parameters) where T : class
        {
            var path = PathFor(endpoint, parameters);
            if (!File.Exists(path)) return null;
            try
            {
                using var fs = File.OpenRead(path);
                var entry = JsonSerializer.Deserialize<FileEntry>(fs, _json);
                if (entry is null) return null;
                if (entry.ExpireAt <= DateTimeOffset.UtcNow)
                {
                    try { File.Delete(path); } catch { }
                    return null;
                }
                if (typeof(T) == typeof(CachedHttpResponse))
                {
                    return (new CachedHttpResponse
                    {
                        StatusCode = entry.StatusCode,
                        ContentType = entry.ContentType,
                        Body = entry.Body ?? Array.Empty<byte>(),
                        ETag = entry.ETag,
                        LastModified = entry.LastModified,
                        StoredAt = entry.StoredAt
                    } as T)!;
                }
                return null;
            }
            catch { return null; }
        }

        public void Set<T>(string endpoint, Dictionary<string, string> parameters, T value) where T : class
            => Set(endpoint, parameters, value, GetCacheDuration(endpoint));

        public void Set<T>(string endpoint, Dictionary<string, string> parameters, T value, TimeSpan duration) where T : class
        {
            if (value is not CachedHttpResponse ch) return;
            var path = PathFor(endpoint, parameters);
            var entry = new FileEntry
            {
                StatusCode = ch.StatusCode,
                ContentType = ch.ContentType,
                Body = ch.Body,
                ETag = ch.ETag,
                LastModified = ch.LastModified,
                StoredAt = DateTimeOffset.UtcNow,
                ExpireAt = DateTimeOffset.UtcNow.Add(duration)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            try
            {
                using (var fs = File.Create(tmp))
                {
                    JsonSerializer.Serialize(fs, entry, _json);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                EnforceLimits();
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public bool ShouldCache(string endpoint) => true;

        public TimeSpan GetCacheDuration(string endpoint) => _defaultDuration;

        public string GenerateCacheKey(string endpoint, Dictionary<string, string> parameters)
        {
            var sb = new StringBuilder();
            sb.Append(endpoint.Trim());
            foreach (var kv in parameters ?? new Dictionary<string, string>())
            {
                sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
            }
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public void Clear()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
            Directory.CreateDirectory(_root);
        }

        public void ClearEndpoint(string endpoint)
        {
            // Coarse: purge all; endpoint partitioning is path-derived
            Clear();
        }

        private string PathFor(string endpoint, Dictionary<string, string> parameters)
        {
            var key = GenerateCacheKey(endpoint, parameters);
            var sub = key[..2];
            return Path.Combine(_root, sub, key + ".json");
        }

        private void TryCleanupExpired()
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                foreach (var file in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        using var fs = File.OpenRead(file);
                        var entry = JsonSerializer.Deserialize<FileEntry>(fs, _json);
                        if (entry is null || entry.ExpireAt <= DateTimeOffset.UtcNow)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        private void EnforceLimits()
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                var files = new List<FileInfo>();
                long total = 0;
                foreach (var file in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
                {
                    try { var fi = new FileInfo(file); files.Add(fi); total += fi.Length; } catch { }
                }
                if ((files.Count <= _maxEntries || _maxEntries <= 0) && (total <= _maxBytes || _maxBytes <= 0)) return;
                foreach (var fi in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    try { total -= fi.Length; File.Delete(fi.FullName); } catch { }
                    if ((files.Count <= _maxEntries || _maxEntries <= 0) && (total <= _maxBytes || _maxBytes <= 0)) break;
                }
            }
            catch { }
        }

        private static int ReadIntEnv(string name, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        private static long ReadLongEnv(string name, long fallback)
            => long.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        private sealed class FileEntry
        {
            public System.Net.HttpStatusCode StatusCode { get; set; }
            public string? ContentType { get; set; }
            public byte[]? Body { get; set; }
            public string? ETag { get; set; }
            public DateTimeOffset? LastModified { get; set; }
            public DateTimeOffset StoredAt { get; set; }
            public DateTimeOffset ExpireAt { get; set; }
        }
    }
}

