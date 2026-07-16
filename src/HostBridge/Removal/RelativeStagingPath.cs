using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Lidarr.Plugin.Common.HostBridge;

public readonly record struct RelativeStagingPath
{
    private RelativeStagingPath(string value) => Value = value;

    public string Value { get; }

    public static RelativeStagingPath Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new ArgumentException("A nonblank relative staging path is required.", nameof(value));
        }

        var segments = value.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Traversal is not permitted.", nameof(value));
        }

        var canonical = string.Join(Path.DirectorySeparatorChar, segments);
        if (Encoding.UTF8.GetByteCount(canonical) > 4096)
        {
            throw new ArgumentException("Relative path exceeds 4096 UTF-8 bytes.", nameof(value));
        }

        return new RelativeStagingPath(canonical);
    }

    public override string ToString() => Value;
}
