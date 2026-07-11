using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.HostBridge;

public sealed class SafeOwnedRoot : IAsyncDisposable
{
    private SafeOwnedRoot(string rootPath, string rootId)
    {
        RootPath = rootPath;
        RootId = rootId;
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
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(trustedBoundaryPath))
            {
                return ValueTask.FromResult(new SafeOwnedRootOpenResult(false, "QUEUE_REMOVAL_ROOT_INVALID", null));
            }

            var root = Canonicalize(rootPath);
            var boundary = Canonicalize(trustedBoundaryPath);
            if (!IsWithin(root, boundary))
            {
                return ValueTask.FromResult(new SafeOwnedRootOpenResult(false, "QUEUE_REMOVAL_ROOT_OUTSIDE_BOUNDARY", null));
            }

            Directory.CreateDirectory(root);
            if (IsLink(root))
            {
                return ValueTask.FromResult(new SafeOwnedRootOpenResult(false, "QUEUE_REMOVAL_ROOT_LINK", null));
            }

            var rootId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
            return ValueTask.FromResult(new SafeOwnedRootOpenResult(true, "QUEUE_REMOVAL_ROOT_OPENED", new SafeOwnedRoot(root, rootId)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(new SafeOwnedRootOpenResult(false, "QUEUE_REMOVAL_ROOT_INVALID", null));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal bool OwnsPath(string path) => IsWithin(Canonicalize(path), RootPath);

    internal static bool IsLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        try
        {
            return new DirectoryInfo(path).LinkTarget is not null
                || new FileInfo(path).LinkTarget is not null;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string candidate, string boundary)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(boundary, comparison)
            || candidate.StartsWith(boundary + Path.DirectorySeparatorChar, comparison);
    }
}

public readonly record struct SafeOwnedRootOpenResult(bool Opened, string Code, SafeOwnedRoot? Root);
