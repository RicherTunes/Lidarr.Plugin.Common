using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lidarr.Plugin.Common.HostBridge;

internal sealed class HostBridgeOwnedStagingHooks
{
    internal Action<string>? BeforeInspectPath { get; set; }
    internal Action<string, string>? BeforeQuarantineMove { get; set; }
    internal Action<string, string>? MoveDirectory { get; set; }
    internal Action<string>? BeforeDeleteEntry { get; set; }
}

internal readonly record struct OwnedStagingQuarantineResult(
    bool CanRemoveRecord,
    bool FilesRemoved,
    string Code,
    string? QuarantinePath);

internal static class OwnedStagingTree
{
    private const string TrashDirectoryName = ".lpc-trash";

    internal static string? ValidateStrictDescendant(string? ownedRoot, string? target) =>
        InspectStrictDescendant(ownedRoot, target, hooks: null).Code;

    internal static OwnedStagingQuarantineResult QuarantineAndDelete(
        string? ownedRoot,
        string? target,
        Guid attemptId,
        long revision,
        HostBridgeOwnedStagingHooks? hooks)
    {
        string root;
        string source;
        try
        {
            root = NormalizeRequired(ownedRoot);
            source = NormalizeRequired(target);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return Refused(HostBridgeQueueResultCodes.SafeOrphanOutsideRoot);
        }

        Inspection sourceInspection;
        try
        {
            sourceInspection = InspectStrictDescendant(root, source, hooks);
        }
        catch (Exception ex) when (IsInspectionException(ex))
        {
            return Refused(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed);
        }

        if (sourceInspection.Code is not null)
            return Refused(sourceInspection.Code);
        if (!sourceInspection.Exists)
            return Success();

        var trash = Path.Combine(root, TrashDirectoryName);
        if (IsStrictDescendant(source, trash))
            return DeleteQuarantine(source, hooks);

        var quarantine = Path.Combine(trash, $"{attemptId:N}-{revision}");
        var createdTrash = false;
        try
        {
            var trashInspection = InspectStrictDescendant(root, trash, hooks);
            if (trashInspection.Code is not null)
                return Refused(trashInspection.Code);
            if (!trashInspection.Exists)
            {
                Directory.CreateDirectory(trash);
                createdTrash = true;
            }

            trashInspection = InspectStrictDescendant(root, trash, hooks);
            if (trashInspection.Code is not null || !trashInspection.Exists)
                return Refused(trashInspection.Code ?? HostBridgeQueueResultCodes.SafeOrphanDeleteFailed);

            var destinationInspection = InspectStrictDescendant(root, quarantine, hooks);
            if (destinationInspection.Code is not null || destinationInspection.Exists)
                return Refused(destinationInspection.Code ?? HostBridgeQueueResultCodes.SafeOrphanDeleteFailed);

            hooks?.BeforeQuarantineMove?.Invoke(source, quarantine);
            if (hooks?.MoveDirectory is { } move)
                move(source, quarantine);
            else
                Directory.Move(source, quarantine);
        }
        catch (Exception ex) when (IsInspectionException(ex))
        {
            if (createdTrash)
                TryRemoveEmptyTrash(trash);
            return Refused(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed);
        }

        Inspection movedInspection;
        try
        {
            movedInspection = InspectStrictDescendant(root, quarantine, hooks);
        }
        catch (Exception ex) when (IsInspectionException(ex))
        {
            return Retained(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed, quarantine);
        }

        if (movedInspection.Code is not null)
            return Retained(movedInspection.Code, quarantine);
        if (!movedInspection.Exists)
            return Retained(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed, quarantine);

        return DeleteQuarantine(quarantine, hooks);
    }

    private static OwnedStagingQuarantineResult DeleteQuarantine(
        string quarantine,
        HostBridgeOwnedStagingHooks? hooks)
    {
        try
        {
            DeleteTreeDepthFirst(quarantine, hooks);
            return Success();
        }
        catch (Exception ex) when (IsInspectionException(ex))
        {
            return Retained(HostBridgeQueueResultCodes.SafeOrphanDeleteFailed, quarantine);
        }
    }

    private static Inspection InspectStrictDescendant(
        string? ownedRoot,
        string? target,
        HostBridgeOwnedStagingHooks? hooks)
    {
        if (string.IsNullOrWhiteSpace(ownedRoot) || string.IsNullOrWhiteSpace(target))
            return new(false, HostBridgeQueueResultCodes.SafeOrphanOutsideRoot);

        string root;
        string candidate;
        try
        {
            root = NormalizeRequired(ownedRoot);
            candidate = NormalizeRequired(target);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return new(false, HostBridgeQueueResultCodes.SafeOrphanOutsideRoot);
        }

        if (!IsStrictDescendant(candidate, root))
            return new(false, HostBridgeQueueResultCodes.SafeOrphanOutsideRoot);

        var rootKind = InspectEntry(root, hooks);
        if (rootKind == EntryKind.Missing)
            throw new DirectoryNotFoundException();
        if (rootKind == EntryKind.Link)
            return new(true, HostBridgeQueueResultCodes.SafeOrphanLinkTraversal);

        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var kind = InspectEntry(current, hooks);
            if (kind == EntryKind.Missing)
                return new(false, null);
            if (kind == EntryKind.Link)
                return new(true, HostBridgeQueueResultCodes.SafeOrphanLinkTraversal);
        }

        var pending = new Stack<string>();
        pending.Push(candidate);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var kind = InspectEntry(entry, hooks);
                if (kind == EntryKind.Link)
                    return new(true, HostBridgeQueueResultCodes.SafeOrphanLinkTraversal);
                if (kind == EntryKind.Directory)
                    pending.Push(entry);
            }
        }

        return new(true, null);
    }

    private static void DeleteTreeDepthFirst(string target, HostBridgeOwnedStagingHooks? hooks)
    {
        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(static path => path, StringComparer.Ordinal))
            {
                var kind = InspectEntry(entry, hooks);
                if (kind == EntryKind.Link)
                    throw new IOException("Link appeared in quarantined tree.");
                if (kind == EntryKind.Directory)
                    pending.Push(entry);
                else if (kind == EntryKind.File)
                    files.Add(entry);
                else
                    throw new IOException("Quarantined entry disappeared during inspection.");
            }
        }

        foreach (var file in files.OrderBy(static path => path, StringComparer.Ordinal))
        {
            hooks?.BeforeDeleteEntry?.Invoke(file);
            File.Delete(file);
        }

        for (var index = directories.Count - 1; index >= 0; index--)
        {
            hooks?.BeforeDeleteEntry?.Invoke(directories[index]);
            Directory.Delete(directories[index], recursive: false);
        }
    }

    private static EntryKind InspectEntry(string path, HostBridgeOwnedStagingHooks? hooks)
    {
        hooks?.BeforeInspectPath?.Invoke(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return LinkTargetExists(path) ? EntryKind.Link : EntryKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return LinkTargetExists(path) ? EntryKind.Link : EntryKind.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return EntryKind.Link;
        return (attributes & FileAttributes.Directory) != 0 ? EntryKind.Directory : EntryKind.File;
    }

    private static bool LinkTargetExists(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsStrictDescendant(string candidate, string root) =>
        !string.Equals(candidate, root, PathComparison) &&
        candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static string NormalizeRequired(string? path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!));

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool IsPathException(Exception exception) => exception is
        ArgumentException or
        NotSupportedException or
        PathTooLongException;

    private static bool IsInspectionException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        ArgumentException or
        NotSupportedException;

    private static void TryRemoveEmptyTrash(string trash)
    {
        try { Directory.Delete(trash, recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static OwnedStagingQuarantineResult Success() =>
        new(true, true, HostBridgeQueueResultCodes.Removed, null);

    private static OwnedStagingQuarantineResult Refused(string code) =>
        new(false, false, code, null);

    private static OwnedStagingQuarantineResult Retained(string code, string quarantine) =>
        new(false, false, code, quarantine);

    private readonly record struct Inspection(bool Exists, string? Code);

    private enum EntryKind { Missing, File, Directory, Link }
}
