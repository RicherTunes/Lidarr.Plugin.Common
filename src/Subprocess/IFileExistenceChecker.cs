// <copyright file="IFileExistenceChecker.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// Abstraction for file existence checks, enabling testability of components
/// that need to verify file paths on the filesystem.
/// </summary>
public interface IFileExistenceChecker
{
    /// <summary>
    /// Determines whether the specified file exists.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the file exists; otherwise, false.</returns>
    bool Exists(string path);
}

/// <summary>
/// Default implementation using the actual filesystem.
/// </summary>
public sealed class FileExistenceChecker : IFileExistenceChecker
{
    /// <inheritdoc />
    public bool Exists(string path) => System.IO.File.Exists(path);
}
