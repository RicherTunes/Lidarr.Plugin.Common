// <copyright file="ClaudeCodeDetector.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Subprocess;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// Detects Claude Code CLI installation across different installation methods.
/// Supports npm global, native installer, and Homebrew installations.
/// </summary>
public class ClaudeCodeDetector
{
    private static readonly TimeSpan PathLookupTimeout = TimeSpan.FromSeconds(5);

    private readonly ICliRunner _cliRunner;
    private readonly IFileExistenceChecker _fileChecker;

    /// <summary>
    /// Possible installation paths for Claude CLI across platforms.
    /// Checked in order after PATH lookup fails.
    /// </summary>
    private static readonly string[] PossiblePaths = BuildPossiblePaths();

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeDetector"/> class.
    /// </summary>
    /// <param name="cliRunner">The CLI runner for executing path lookup commands.</param>
    /// <param name="fileChecker">Optional file existence checker (defaults to real filesystem).</param>
    public ClaudeCodeDetector(ICliRunner cliRunner, IFileExistenceChecker? fileChecker = null)
    {
        _cliRunner = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));
        _fileChecker = fileChecker ?? new FileExistenceChecker();
    }

    /// <summary>
    /// Finds the Claude CLI binary path.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The full path to the Claude CLI binary, or null if not installed.
    /// </returns>
    /// <remarks>
    /// Detection strategy:
    /// <list type="number">
    /// <item>Check PATH using "which" (Unix) or "where" (Windows)</item>
    /// <item>Check known installation paths on the filesystem</item>
    /// </list>
    /// </remarks>
    public async Task<string?> FindClaudeCliAsync(CancellationToken ct = default)
    {
        // Strategy 1: Check PATH
        var pathResult = await TryFindInPathAsync(ct).ConfigureAwait(false);
        if (pathResult != null)
        {
            return pathResult;
        }

        // Strategy 2: Check known installation paths
        foreach (var path in PossiblePaths)
        {
            if (_fileChecker.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private async Task<string?> TryFindInPathAsync(CancellationToken ct)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var command = isWindows ? "where" : "which";

        var options = new CliRunnerOptions
        {
            Timeout = PathLookupTimeout,
            ThrowOnNonZeroExitCode = false,
        };

        try
        {
            var result = await _cliRunner.ExecuteAsync(
                command,
                new[] { "claude" },
                options,
                ct).ConfigureAwait(false);

            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                // Return first line (where/which may return multiple paths)
                var firstLine = result.StandardOutput.Split('\n')[0].Trim();
                if (!string.IsNullOrEmpty(firstLine) && _fileChecker.Exists(firstLine))
                {
                    return firstLine;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation
            throw;
        }
        catch
        {
            // Command not found or other error - continue to filesystem check
        }

        return null;
    }

    private static string[] BuildPossiblePaths()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new[]
        {
            // Native installation (preferred) - Unix
            Path.Combine(userProfile, ".local", "bin", "claude"),

            // Native installation - Windows
            Path.Combine(userProfile, ".local", "bin", "claude.exe"),

            // AppData Local - Windows native installer
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe"),

            // Common Linux paths
            "/usr/local/bin/claude",
            "/usr/bin/claude",

            // Homebrew on macOS (Apple Silicon)
            "/opt/homebrew/bin/claude",

            // Homebrew on macOS (Intel)
            "/usr/local/Homebrew/bin/claude",

            // npm global installation paths - Unix
            Path.Combine(userProfile, ".npm-global", "bin", "claude"),
            "/usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude",

            // npm global installation paths - Windows
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        };
    }
}
