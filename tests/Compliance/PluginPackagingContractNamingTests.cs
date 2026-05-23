using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;
using Xunit.Sdk;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Unit tests for <see cref="PluginPackagingContract.AssertMainDllMatchesLoaderNamingConvention"/>.
/// Covers the Lidarr PluginLoader naming contract (Lidarr.Plugin.*.dll glob) that caught
/// the May 2026 AppleMusicarr incident where a misnamed plugin DLL silently failed to load.
/// </summary>
public class PluginPackagingContractNamingTests
{
    [Theory]
    [InlineData("Lidarr.Plugin.Brainarr.dll")]
    [InlineData("Lidarr.Plugin.AppleMusicarr.dll")]
    [InlineData("lidarr.plugin.lowercase.dll")] // case-insensitive
    [InlineData("LIDARR.PLUGIN.UPPER.DLL")]
    public void AssertMainDllMatchesLoaderNamingConvention_AcceptsCompliantNames(string dllName)
    {
        var policy = MakePolicy(dllName);
        PluginPackagingContract.AssertMainDllMatchesLoaderNamingConvention(policy);
    }

    [Theory]
    [InlineData("AppleMusicarr.Plugin.dll")] // the May 2026 incident
    [InlineData("MyPlugin.dll")]
    [InlineData("Plugin.dll")]
    [InlineData("Lidarr.Plugin")] // missing .dll
    [InlineData("Lidarr.Plugin.")] // technically matches but no name component — accepted by glob though
    public void AssertMainDllMatchesLoaderNamingConvention_RejectsNonCompliantNames(string dllName)
    {
        var policy = MakePolicy(dllName);
        // Lidarr.Plugin. (with trailing dot, no name, no .dll) IS rejected because
        // it does not end with .dll. Pure-string check: starts with prefix AND ends with .dll.
        Assert.Throws<TrueException>(
            () => PluginPackagingContract.AssertMainDllMatchesLoaderNamingConvention(policy));
    }

    [Fact]
    public void AssertMainDllMatchesLoaderNamingConvention_ErrorMessageMentionsLoaderSource()
    {
        var policy = MakePolicy("AppleMusicarr.Plugin.dll");
        var ex = Assert.Throws<TrueException>(
            () => PluginPackagingContract.AssertMainDllMatchesLoaderNamingConvention(policy));
        Assert.Contains("Lidarr.Plugin.*.dll", ex.Message);
        Assert.Contains("PathExtensions.cs:334", ex.Message);
        Assert.Contains("AssemblyName", ex.Message);
    }

    [Fact]
    public void AssertMainDllMatchesLoaderNamingConvention_NullPolicy_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PluginPackagingContract.AssertMainDllMatchesLoaderNamingConvention(null!));
    }

    private static PluginPackagePolicy MakePolicy(string mainDll) => new(
        MainDllName: mainDll,
        RequiredFiles: new List<string> { mainDll, "plugin.json" },
        ForbiddenAssemblies: Array.Empty<string>(),
        MainDllMinimumBytes: 0);
}
