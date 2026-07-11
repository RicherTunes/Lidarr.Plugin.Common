using System;
using System.Collections.Generic;
using System.IO;

namespace Lidarr.Plugin.Common.HostBridge;

internal readonly record struct OwnedStagingTreeDeleteResult(
    bool Allowed,
    bool FilesRemoved,
    string Code);

internal static class OwnedStagingTree
{
    internal static string? ValidateStrictDescendant(string? ownedRoot, string? target)
    {
        if (string.IsNullOrWhiteSpace(ownedRoot) || string.IsNullOrWhiteSpace(target))
            return HostBridgeQueueResultCodes.SafeOrphanOutsideRoot;

        string canonicalRoot;
        string canonicalTarget;
        try
        {
            canonicalRoot = Normalize(ownedRoot);
            canonicalTarget = Normalize(target);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return HostBridgeQueueResultCodes.SafeOrphanOutsideRoot;
        }

        if (SamePath(canonicalRoot, canonicalTarget) ||
            !canonicalTarget.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison))
        {
            return HostBridgeQueueResultCodes.SafeOrphanOutsideRoot;
        }

        foreach (var component in ExistingComponents(canonicalRoot, canonicalTarget))
        {
            if (IsReparsePoint(component))
                return HostBridgeQueueResultCodes.SafeOrphanLinkTraversal;
        }

        if (!Directory.Exists(canonicalTarget))
            return null;

        var pending = new Stack<string>();
        pending.Push(canonicalTarget);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return HostBridgeQueueResultCodes.SafeOrphanLinkTraversal;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }

        return null;
    }

    internal static OwnedStagingTreeDeleteResult DeleteValidatedTree(
        string? ownedRoot,
        string? target)
    {
        var refusalCode = ValidateStrictDescendant(ownedRoot, target);
        if (refusalCode is not null)
            return new(false, false, refusalCode);

        var canonicalTarget = Normalize(target!);
        if (!Directory.Exists(canonicalTarget))
            return new(true, true, HostBridgeQueueResultCodes.Removed);

        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(canonicalTarget);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return new(false, false, HostBridgeQueueResultCodes.SafeOrphanLinkTraversal);
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    File.Delete(entry);
            }
        }

        for (var index = directories.Count - 1; index >= 0; index--)
            Directory.Delete(directories[index], recursive: false);

        return new(true, true, HostBridgeQueueResultCodes.Removed);
    }

    private static IEnumerable<string> ExistingComponents(string canonicalRoot, string canonicalTarget)
    {
        if (ExistsOrIsLink(canonicalRoot))
            yield return canonicalRoot;

        var relative = Path.GetRelativePath(canonicalRoot, canonicalTarget);
        var current = canonicalRoot;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (ExistsOrIsLink(current))
                yield return current;
            else
                yield break;
        }
    }

    private static bool ExistsOrIsLink(string path) =>
        Directory.Exists(path) || File.Exists(path) || IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool IsPathException(Exception exception) => exception is
        ArgumentException or
        NotSupportedException or
        PathTooLongException or
        IOException or
        UnauthorizedAccessException;
}
