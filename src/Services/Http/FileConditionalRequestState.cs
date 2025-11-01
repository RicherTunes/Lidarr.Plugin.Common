using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// File-backed conditional validator storage (ETag/Last-Modified) keyed by cache key string.
    /// </summary>
    public sealed class FileConditionalRequestState : IConditionalRequestState
    {
        private readonly string _folder;
        private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public FileConditionalRequestState(string? folder = null)
        {
            _folder = folder ?? Lidarr.Plugin.Common.Utilities.PluginDataFolders.For("etag-validators");
            Directory.CreateDirectory(_folder);
        }

        public async ValueTask<(string? ETag, DateTimeOffset? LastModified)?> TryGetValidatorsAsync(string cacheKey, CancellationToken cancellationToken = default)
        {
            var path = PathFor(cacheKey);
            if (!File.Exists(path)) return null;
            var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var fs = File.OpenRead(path);
                var model = await JsonSerializer.DeserializeAsync<Model>(fs, _json, cancellationToken).ConfigureAwait(false);
                return model is null ? null : (model.ETag, model.LastModified);
            }
            catch { return null; }
            finally { gate.Release(); }
        }

        public async ValueTask SetValidatorsAsync(string cacheKey, string? eTag, DateTimeOffset? lastModified, CancellationToken cancellationToken = default)
        {
            var path = PathFor(cacheKey);
            var model = new Model { ETag = eTag, LastModified = lastModified, StoredAt = DateTimeOffset.UtcNow };
            var tmp = path + ".tmp";
            var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var fs = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(fs, model, _json, cancellationToken).ConfigureAwait(false);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            finally { gate.Release(); }
        }

        private string PathFor(string key)
        {
            var safe = key.Replace('/', '_').Replace(':', '_');
            var sub = safe.Length > 2 ? safe[..2] : "xx";
            Directory.CreateDirectory(Path.Combine(_folder, sub));
            return Path.Combine(_folder, sub, safe + ".json");
        }

        private sealed class Model
        {
            public string? ETag { get; set; }
            public DateTimeOffset? LastModified { get; set; }
            public DateTimeOffset StoredAt { get; set; }
        }
    }
}

