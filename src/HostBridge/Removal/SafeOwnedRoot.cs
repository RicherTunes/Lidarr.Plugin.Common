using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

public sealed class SafeOwnedRoot : IAsyncDisposable
{
    internal const string RootMarkerFileName = ".lpc-root-id";

    private readonly string _trustedBoundaryPath;
    private readonly string _markerPath;
    // FileShare excludes delete/overwrite for the lifetime on Windows. Unix does not enforce
    // rename exclusion on open handles, so every journal operation also revalidates the path,
    // ancestors, and exact marker bytes. Malicious mutation by this same OS identity remains
    // outside the documented threat model.
    private readonly FileStream _markerStream;
    private bool _disposed;

    private SafeOwnedRoot(
        string rootPath,
        string trustedBoundaryPath,
        string rootId,
        string markerPath,
        FileStream markerStream)
    {
        RootPath = rootPath;
        _trustedBoundaryPath = trustedBoundaryPath;
        RootId = rootId;
        _markerPath = markerPath;
        _markerStream = markerStream;
        RootMarkerIdentity = new FileIdentity(1, rootId, rootId, FileIdentityEntryType.Directory);
        Capability = StagingDeletionCapability.QuarantineOnly;
    }

    public string RootId { get; }

    public FileIdentity RootMarkerIdentity { get; }

    public StagingDeletionCapability Capability { get; }

    internal string RootPath { get; }

    public static ValueTask<SafeOwnedRootOpenResult> OpenAsync(
        string rootPath,
        string trustedBoundaryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? markerStream = null;
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(trustedBoundaryPath))
                return ValueTask.FromResult(Failed("QUEUE_REMOVAL_ROOT_INVALID"));

            var root = Canonicalize(rootPath);
            var boundary = Canonicalize(trustedBoundaryPath);
            if (!IsWithin(root, boundary))
                return ValueTask.FromResult(Failed("QUEUE_REMOVAL_ROOT_OUTSIDE_BOUNDARY"));

            Directory.CreateDirectory(root);
            if (!HasUnlinkedAncestors(root) || !HasUnlinkedAncestors(boundary))
                return ValueTask.FromResult(Failed("QUEUE_REMOVAL_ROOT_LINK"));

            var markerPath = Path.Combine(root, RootMarkerFileName);
            markerStream = OpenOrCreateMarker(markerPath);
            var marker = ReadMarkerExactly(markerStream);
            if (!HasStrictMarker(marker) || IsLink(markerPath))
            {
                markerStream.Dispose();
                return ValueTask.FromResult(Failed("QUEUE_REMOVAL_ROOT_INVALID"));
            }

            if (!HasStrictMarkerPermissions(markerPath))
            {
                markerStream.Dispose();
                return ValueTask.FromResult(Failed("QUEUE_REMOVAL_ROOT_INVALID"));
            }

            var ownedRoot = new SafeOwnedRoot(root, boundary, marker, markerPath, markerStream);
            markerStream = null;
            return ValueTask.FromResult(new SafeOwnedRootOpenResult(
                true,
                "QUEUE_REMOVAL_ROOT_OPENED",
                ownedRoot));
        }
        catch (Exception error) when (error is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            markerStream?.Dispose();
            return ValueTask.FromResult(Failed("QUEUE_REMOVAL_ROOT_INVALID"));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _markerStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal RemovalJournalError RevalidateForJournal()
    {
        if (_disposed || _markerStream.SafeFileHandle.IsClosed || _markerStream.SafeFileHandle.IsInvalid)
            return RemovalJournalError.SecondWriter;

        try
        {
            if (Canonicalize(RootPath) != RootPath
                || !IsWithin(RootPath, _trustedBoundaryPath)
                || !HasUnlinkedAncestors(RootPath)
                || !HasUnlinkedAncestors(_trustedBoundaryPath)
                || IsLink(_markerPath)
                || !HasStrictMarkerPermissions(_markerPath))
            {
                return RemovalJournalError.Corrupt;
            }

            using var current = new FileStream(
                _markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            return string.Equals(ReadMarkerExactly(current), RootId, StringComparison.Ordinal)
                ? RemovalJournalError.None
                : RemovalJournalError.Corrupt;
        }
        catch (Exception error) when (error is
            FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return RemovalJournalError.Corrupt;
        }
    }

    internal bool OwnsPath(string path) => IsWithin(Canonicalize(path), RootPath);

    internal static bool IsLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        try
        {
            return new DirectoryInfo(path).LinkTarget is not null
                || new FileInfo(path).LinkTarget is not null;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static FileStream OpenOrCreateMarker(string markerPath)
    {
        try
        {
            return OpenExistingMarker(markerPath);
        }
        catch (FileNotFoundException)
        {
            return CreateMarker(markerPath);
        }
    }

    private static FileStream OpenExistingMarker(string markerPath) => new(
        markerPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.SequentialScan);

    private static FileStream CreateMarker(string markerPath)
    {
        FileStream created;
        if (OperatingSystem.IsWindows())
        {
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
            var security = new FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            created = new FileInfo(markerPath).Create(
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough,
                security);
        }
        else
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = FileOptions.WriteThrough,
            };
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            created = new FileStream(markerPath, options);
        }

        InitializeNewMarker(created).Dispose();
        return OpenExistingMarker(markerPath);
    }

    private static FileStream InitializeNewMarker(FileStream stream)
    {
        try
        {
            Span<byte> random = stackalloc byte[32];
            RandomNumberGenerator.Fill(random);
            var marker = Convert.ToHexString(random).ToLowerInvariant();
            var bytes = Encoding.ASCII.GetBytes(marker);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            if (!string.Equals(ReadMarkerExactly(stream), marker, StringComparison.Ordinal))
                throw new IOException("Root marker readback failed.");
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool HasStrictMarkerPermissions(string markerPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.GetUnixFileMode(markerPath)
                == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null) return false;
        var security = new FileInfo(markerPath).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        if (!currentUser.Equals(security.GetOwner(typeof(SecurityIdentifier)))
            || !security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IsInherited
                || rule.AccessControlType != AccessControlType.Allow
                || rule.FileSystemRights != FileSystemRights.FullControl
                || !currentUser.Equals(rule.IdentityReference))
            {
                return false;
            }
        }

        return rules.Count == 1;
    }

    private static string ReadMarkerExactly(FileStream stream)
    {
        stream.Position = 0;
        Span<byte> bytes = stackalloc byte[65];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes[count..]);
            if (read == 0) break;
            count += read;
        }

        stream.Position = 0;
        return count == 64 ? Encoding.ASCII.GetString(bytes[..64]) : string.Empty;
    }

    private static bool HasStrictMarker(string marker)
    {
        if (marker.Length != 64) return false;
        foreach (var character in marker)
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }

        return true;
    }

    private static bool HasUnlinkedAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (IsLink(current.FullName)) return false;
        }

        return true;
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string candidate, string boundary)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(boundary, comparison)
            || candidate.StartsWith(boundary + Path.DirectorySeparatorChar, comparison);
    }

    private static SafeOwnedRootOpenResult Failed(string code) => new(false, code, null);
}

public readonly record struct SafeOwnedRootOpenResult(bool Opened, string Code, SafeOwnedRoot? Root);
