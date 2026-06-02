using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Hosting;
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
        // Bounded lock striping: a fixed set of gates hashed by cache key, rather than one
        // SemaphoreSlim per distinct key. The previous ConcurrentDictionary<string,SemaphoreSlim>
        // added (and never removed) a lock for every distinct cache key, growing without bound
        // across a long-running process. A fixed stripe set caps memory while still serializing
        // access per file (rare cross-key collisions merely share a stripe).
        private readonly SemaphoreSlim[] _locks = CreateStripes(64);

        private static SemaphoreSlim[] CreateStripes(int count)
        {
            var stripes = new SemaphoreSlim[count];
            for (var i = 0; i < count; i++)
            {
                stripes[i] = new SemaphoreSlim(1, 1);
            }

            return stripes;
        }

        private SemaphoreSlim GateFor(string path)
            => _locks[(int)((uint)StringComparer.Ordinal.GetHashCode(path) % (uint)_locks.Length)];

        public FileConditionalRequestState(string? folder = null)
        {
            // When no explicit folder is supplied, resolve the canonical per-host config root
            // (Docker /config, %AppData%, XDG_CONFIG_HOME, $HOME/.config, ...) so empty $HOME
            // inside Lidarr's Docker container doesn't anchor a relative path at /app/bin.
            // "ArrPlugins" remains the shared cross-plugin root; "etag-validators" is the per-feature leaf.
            _folder = folder ?? Path.Combine(PluginConfigRoots.Resolve("ArrPlugins"), "etag-validators");
            Directory.CreateDirectory(_folder);
        }

        public async ValueTask<(string? ETag, DateTimeOffset? LastModified)?> TryGetValidatorsAsync(string cacheKey, CancellationToken cancellationToken = default)
        {
            var path = PathFor(cacheKey);
            if (!File.Exists(path)) return null;
            var gate = GateFor(path);
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
            var gate = GateFor(path);
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

