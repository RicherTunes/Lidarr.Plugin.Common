// <copyright file="PluginConfigRootsTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using Lidarr.Plugin.Common.Hosting;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Storage;

public class PluginConfigRootsTests
{
    private sealed class FakeEnv : IConfigEnvironment
    {
        public Dictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new();

        public HashSet<string> ExistingDirectories { get; } = new(StringComparer.Ordinal);

        public string? GetEnvironmentVariable(string name)
            => Vars.TryGetValue(name, out var v) ? v : null;

        public string GetFolderPath(Environment.SpecialFolder folder)
            => Folders.TryGetValue(folder, out var v) ? v : string.Empty;

        public bool DirectoryExists(string path)
            => ExistingDirectories.Contains(path);
    }

    [Fact]
    public void Resolve_NullAppName_Throws()
    {
        Assert.Throws<ArgumentException>(() => PluginConfigRoots.Resolve(null!));
    }

    [Fact]
    public void Resolve_WhitespaceAppName_Throws()
    {
        Assert.Throws<ArgumentException>(() => PluginConfigRoots.Resolve("   "));
    }

    [Fact]
    public void Resolve_OverrideEnvVar_TakesPrecedence()
    {
        var env = new FakeEnv();
        env.Vars[PluginConfigRoots.OverrideEnvVar] = "/some/override";
        env.ExistingDirectories.Add(PluginConfigRoots.DefaultDockerConfigRoot); // even if /config exists, override wins

        var result = PluginConfigRoots.Resolve("MyPlugin", env);

        Assert.Equal(Path.Combine("/some/override", "MyPlugin"), result);
    }

    [Fact]
    public void Resolve_DockerConfigRootPresent_PrefersIt()
    {
        var env = new FakeEnv();
        env.ExistingDirectories.Add(PluginConfigRoots.DefaultDockerConfigRoot);
        env.Folders[Environment.SpecialFolder.ApplicationData] = "C:/Users/test/AppData/Roaming";
        env.Vars["HOME"] = "/root"; // common Docker pitfall

        var result = PluginConfigRoots.Resolve("MyPlugin", env);

        Assert.Equal(Path.Combine(PluginConfigRoots.DefaultDockerConfigRoot, "MyPlugin"), result);
    }

    [Fact]
    public void Resolve_WindowsAppData_UsedWhenAvailable()
    {
        var env = new FakeEnv();
        env.Folders[Environment.SpecialFolder.ApplicationData] = @"C:\Users\test\AppData\Roaming";

        var result = PluginConfigRoots.Resolve("MyPlugin", env);

        Assert.Equal(Path.Combine(@"C:\Users\test\AppData\Roaming", "MyPlugin"), result);
    }

    [Fact]
    public void Resolve_XdgConfigHome_UsedBeforeHomeFallback()
    {
        var env = new FakeEnv();
        // Simulate Linux: no AppData, no /config
        env.Vars["XDG_CONFIG_HOME"] = "/home/user/.local/config";
        env.Vars["HOME"] = "/home/user";

        var result = PluginConfigRoots.Resolve("MyPlugin", env);

        Assert.Equal(Path.Combine("/home/user/.local/config", "MyPlugin"), result);
    }

    [Fact]
    public void Resolve_HomeFallback_WhenNoXdgConfigHome()
    {
        var env = new FakeEnv();
        env.Vars["HOME"] = "/home/user";

        var result = PluginConfigRoots.Resolve("MyPlugin", env);

        Assert.Equal(Path.Combine("/home/user", ".config", "MyPlugin"), result);
    }

    [Fact]
    public void Resolve_LastResort_UsesDockerRoot()
    {
        var env = new FakeEnv();
        // No env vars, no AppData, no existing /config

        var result = PluginConfigRoots.Resolve("MyPlugin", env);

        Assert.Equal(Path.Combine(PluginConfigRoots.DefaultDockerConfigRoot, "MyPlugin"), result);
    }

    [Fact]
    public void Resolve_Default_WithRealEnvironment_ReturnsAbsolutePath()
    {
        // Smoke test against the process environment
        var result = PluginConfigRoots.Resolve("LpcSmokeTest");
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.EndsWith("LpcSmokeTest", result);
    }
}
