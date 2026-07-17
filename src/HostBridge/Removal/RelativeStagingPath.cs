using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Lidarr.Plugin.Common.HostBridge;

public readonly record struct RelativeStagingPath
{
    // The canonical wire separator is always '/', independent of the writing host's
    // Path.DirectorySeparatorChar. Both '/' and '\' are accepted on input so a state file
    // written on Windows (which historically joined with '\') still parses on Linux and vice
    // versa (5B item 4: OS-portable persisted paths). Use ToNativeRelativePath() to project
    // the canonical value back onto the current host's separator for filesystem calls.
    private const char CanonicalSeparator = '/';
    private static readonly char[] AcceptedSeparators = { '/', '\\' };

    private RelativeStagingPath(string value) => Value = value;

    public string Value { get; }

    public static RelativeStagingPath Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value[0] is '/' or '\\'
            || HasDriveQualifier(value))
        {
            throw new ArgumentException("A nonblank relative staging path is required.", nameof(value));
        }

        var segments = value.Split(AcceptedSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Traversal is not permitted.", nameof(value));
        }

        var canonical = string.Join(CanonicalSeparator, segments);
        if (Encoding.UTF8.GetByteCount(canonical) > 4096)
        {
            throw new ArgumentException("Relative path exceeds 4096 UTF-8 bytes.", nameof(value));
        }

        return new RelativeStagingPath(canonical);
    }

    // Rejects a Windows drive-qualified path (e.g. "C:foo") on every OS. Path.IsPathRooted only
    // recognizes the drive qualifier on Windows, so this keeps the rejection portable.
    private static bool HasDriveQualifier(string value) =>
        value.Length >= 2
        && value[1] == ':'
        && ((value[0] >= 'a' && value[0] <= 'z') || (value[0] >= 'A' && value[0] <= 'Z'));

    /// <summary>
    /// Projects the OS-portable canonical value (always forward-slash separated) onto the
    /// current host's directory separator for use in <see cref="System.IO.Path.Combine(string, string)"/>.
    /// </summary>
    public string ToNativeRelativePath() => Value.Replace('/', Path.DirectorySeparatorChar);

    public override string ToString() => Value;
}
