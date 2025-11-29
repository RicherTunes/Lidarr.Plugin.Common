# Build Your First Lidarr Plugin

This tutorial walks you through creating a complete Lidarr streaming plugin from scratch using the `Lidarr.Plugin.Common` library. By the end, you'll have a working plugin with an indexer, settings UI, and proper error handling.

**Time**: ~30 minutes
**Prerequisites**: .NET 8 SDK, basic C# knowledge
**Outcome**: A deployable Lidarr plugin

---

## Table of Contents

1. [Understanding the Architecture](#1-understanding-the-architecture)
2. [Project Setup](#2-project-setup)
3. [Creating the Plugin Entry Point](#3-creating-the-plugin-entry-point)
4. [Implementing Settings](#4-implementing-settings)
5. [Building the Indexer](#5-building-the-indexer)
6. [Adding the Plugin Manifest](#6-adding-the-plugin-manifest)
7. [Testing Your Plugin](#7-testing-your-plugin)
8. [Packaging for Distribution](#8-packaging-for-distribution)
9. [Common Issues & Solutions](#9-common-issues--solutions)
10. [Next Steps](#10-next-steps)

---

## 1. Understanding the Architecture

Before writing code, understand how plugins work:

```
┌─────────────────────────────────────────────────────────┐
│                      Lidarr Host                        │
│  ┌─────────────────────────────────────────────────┐   │
│  │        Lidarr.Plugin.Abstractions               │   │
│  │  (Shared interfaces: IPlugin, IIndexer, etc.)   │   │
│  └─────────────────────────────────────────────────┘   │
│                          │                              │
│                    loads via ALC                        │
│                          ▼                              │
│  ┌─────────────────────────────────────────────────┐   │
│  │              Your Plugin (isolated)              │   │
│  │  ┌─────────────────────────────────────────┐    │   │
│  │  │       Lidarr.Plugin.Common              │    │   │
│  │  │  (Base classes, HTTP, auth, caching)    │    │   │
│  │  └─────────────────────────────────────────┘    │   │
│  │  ┌─────────────────────────────────────────┐    │   │
│  │  │       Your Code                         │    │   │
│  │  │  (MyPlugin.dll, MyIndexer, etc.)        │    │   │
│  │  └─────────────────────────────────────────┘    │   │
│  └─────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

**Key concepts**:
- **Abstractions**: Stable interfaces shared between host and plugin
- **Common**: Helper library shipped WITH your plugin (not by the host)
- **Isolation**: Each plugin runs in its own AssemblyLoadContext (ALC)

---

## 2. Project Setup

### Create the project structure

```bash
mkdir MyStreamingPlugin
cd MyStreamingPlugin

# Create solution and projects
dotnet new sln -n MyStreamingPlugin
dotnet new classlib -n MyStreamingPlugin -f net8.0
dotnet sln add MyStreamingPlugin/MyStreamingPlugin.csproj

# Create test project (optional but recommended)
dotnet new xunit -n MyStreamingPlugin.Tests -f net8.0
dotnet sln add MyStreamingPlugin.Tests/MyStreamingPlugin.Tests.csproj
dotnet add MyStreamingPlugin.Tests reference MyStreamingPlugin
```

### Configure the project file

Edit `MyStreamingPlugin/MyStreamingPlugin.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- CRITICAL: Ensures all dependencies are copied to output -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>

    <!-- Plugin metadata -->
    <Version>1.0.0</Version>
    <Authors>Your Name</Authors>
    <Description>My streaming service plugin for Lidarr</Description>
  </PropertyGroup>

  <ItemGroup>
    <!-- Abstractions: compile-time only, host provides at runtime -->
    <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.2.0"
                      PrivateAssets="all"
                      ExcludeAssets="runtime;native;contentfiles" />

    <!-- Common: ships with your plugin -->
    <PackageReference Include="Lidarr.Plugin.Common" Version="1.2.2" />
  </ItemGroup>

  <ItemGroup>
    <!-- Copy plugin.json to output -->
    <None Update="plugin.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

**Why these settings matter**:

| Setting | Purpose |
|---------|---------|
| `CopyLocalLockFileAssemblies` | Ensures Common.dll and dependencies are in your output folder |
| `PrivateAssets="all"` | Prevents shipping duplicate Abstractions assembly |
| `ExcludeAssets="runtime"` | Host provides Abstractions at runtime |

---

## 3. Creating the Plugin Entry Point

Create `MyStreamingPlugin/MyPlugin.cs`:

```csharp
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MyStreamingPlugin;

/// <summary>
/// Main plugin entry point. Lidarr loads this class via reflection.
/// Inheriting from StreamingPlugin gives you:
/// - Automatic DI container setup
/// - Settings management
/// - Lifecycle hooks
/// </summary>
public sealed class MyPlugin : StreamingPlugin<MyPluginModule, MyPluginSettings>
{
    /// <summary>
    /// Register your services here. Called once during plugin initialization.
    /// </summary>
    protected override void ConfigureServices(
        IServiceCollection services,
        IPluginContext context,
        MyPluginSettings settings)
    {
        // Register your indexer as a singleton (one instance for the plugin lifetime)
        services.AddSingleton<MyIndexer>();

        // Register HTTP client with resilience policies
        services.AddHttpClient<MyApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.example.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "MyStreamingPlugin/1.0");
        });

        // You can also register:
        // - Scoped services (per-request)
        // - Transient services (new instance each time)
        // - Configuration options
    }

    /// <summary>
    /// Create the indexer when Lidarr requests it.
    /// Return null if your plugin doesn't provide an indexer.
    /// </summary>
    protected override ValueTask<IIndexer?> CreateIndexerAsync(
        MyPluginSettings settings,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var indexer = services.GetRequiredService<MyIndexer>();
        return ValueTask.FromResult<IIndexer?>(indexer);
    }

    /// <summary>
    /// Create the download client when Lidarr requests it.
    /// Return null if your plugin doesn't provide a download client.
    /// </summary>
    protected override ValueTask<IDownloadClient?> CreateDownloadClientAsync(
        MyPluginSettings settings,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // We'll add this in a later tutorial
        return ValueTask.FromResult<IDownloadClient?>(null);
    }
}
```

Create `MyStreamingPlugin/MyPluginModule.cs`:

```csharp
using Lidarr.Plugin.Common.Services.Registration;

namespace MyStreamingPlugin;

/// <summary>
/// Plugin metadata shown in Lidarr's UI.
/// This is NOT the same as plugin.json - this is runtime metadata.
/// </summary>
public sealed class MyPluginModule : StreamingPluginModule
{
    public override string ServiceName => "My Streaming Service";
    public override string Description => "Search and download from My Streaming Service";
    public override string Author => "Your Name";

    // Optional: provide a link to documentation
    public override string? HelpLink => "https://github.com/yourname/mystreaming-plugin";
}
```

---

## 4. Implementing Settings

Settings define what users configure in Lidarr's UI.

Create `MyStreamingPlugin/MyPluginSettings.cs`:

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MyStreamingPlugin;

/// <summary>
/// User-configurable settings. These appear in Lidarr's Settings UI.
/// Use data annotations for validation.
/// </summary>
public sealed class MyPluginSettings
{
    /// <summary>
    /// API key from the streaming service.
    /// The [PasswordPropertyText] attribute masks input in the UI.
    /// </summary>
    [Required(ErrorMessage = "API Key is required")]
    [PasswordPropertyText]
    [DisplayName("API Key")]
    [Description("Your API key from the streaming service dashboard")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// User's preferred audio quality.
    /// </summary>
    [DisplayName("Preferred Quality")]
    [Description("Audio quality for downloads")]
    [DefaultValue(AudioQuality.Lossless)]
    public AudioQuality PreferredQuality { get; set; } = AudioQuality.Lossless;

    /// <summary>
    /// Geographic region for content availability.
    /// </summary>
    [DisplayName("Region")]
    [Description("Your geographic region for content filtering")]
    [DefaultValue("US")]
    public string Region { get; set; } = "US";

    /// <summary>
    /// Enable debug logging for troubleshooting.
    /// </summary>
    [DisplayName("Enable Debug Logging")]
    [Description("Log detailed API responses for troubleshooting")]
    [DefaultValue(false)]
    public bool EnableDebugLogging { get; set; } = false;
}

/// <summary>
/// Audio quality options shown as a dropdown in the UI.
/// </summary>
public enum AudioQuality
{
    [Description("Standard (MP3 320kbps)")]
    Standard = 0,

    [Description("High (FLAC 16-bit/44.1kHz)")]
    High = 1,

    [Description("Lossless (FLAC 24-bit/96kHz)")]
    Lossless = 2,

    [Description("Hi-Res (FLAC 24-bit/192kHz)")]
    HiRes = 3
}
```

---

## 5. Building the Indexer

The indexer handles search requests from Lidarr.

Create `MyStreamingPlugin/MyIndexer.cs`:

```csharp
using System.Runtime.CompilerServices;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace MyStreamingPlugin;

/// <summary>
/// Handles search requests from Lidarr.
/// Implements IIndexer to integrate with Lidarr's search system.
/// </summary>
public sealed class MyIndexer : IIndexer
{
    private readonly ILogger<MyIndexer> _logger;
    private readonly MyApiClient _apiClient;
    private readonly MyPluginSettings _settings;

    public MyIndexer(
        ILogger<MyIndexer> logger,
        MyApiClient apiClient,
        MyPluginSettings settings)
    {
        _logger = logger;
        _apiClient = apiClient;
        _settings = settings;
    }

    /// <summary>
    /// Called when the indexer is first loaded. Validate credentials here.
    /// </summary>
    public async ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing MyIndexer...");

        // Validate API key
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return PluginValidationResult.Failure("API Key is required");
        }

        // Test the connection
        try
        {
            var isValid = await _apiClient.TestConnectionAsync(cancellationToken);
            if (!isValid)
            {
                return PluginValidationResult.Failure("Invalid API Key or service unavailable");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to streaming service");
            return PluginValidationResult.Failure($"Connection failed: {ex.Message}");
        }

        _logger.LogInformation("MyIndexer initialized successfully");
        return PluginValidationResult.Success();
    }

    /// <summary>
    /// Search for albums matching the query.
    /// </summary>
    public async ValueTask<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Searching albums for: {Query}", query);

        try
        {
            var results = await _apiClient.SearchAlbumsAsync(query, cancellationToken);

            _logger.LogInformation("Found {Count} albums for query: {Query}",
                results.Count, query);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Album search failed for: {Query}", query);
            return Array.Empty<StreamingAlbum>();
        }
    }

    /// <summary>
    /// Search for tracks matching the query.
    /// </summary>
    public async ValueTask<IReadOnlyList<StreamingTrack>> SearchTracksAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Searching tracks for: {Query}", query);

        try
        {
            var results = await _apiClient.SearchTracksAsync(query, cancellationToken);

            _logger.LogInformation("Found {Count} tracks for query: {Query}",
                results.Count, query);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Track search failed for: {Query}", query);
            return Array.Empty<StreamingTrack>();
        }
    }

    /// <summary>
    /// Get a specific album by its ID.
    /// </summary>
    public async ValueTask<StreamingAlbum?> GetAlbumAsync(
        string albumId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting album: {AlbumId}", albumId);

        try
        {
            return await _apiClient.GetAlbumAsync(albumId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get album: {AlbumId}", albumId);
            return null;
        }
    }

    /// <summary>
    /// Stream album results for large result sets (memory efficient).
    /// </summary>
    public async IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var albums = await SearchAlbumsAsync(query, cancellationToken);
        foreach (var album in albums)
        {
            yield return album;
        }
    }

    /// <summary>
    /// Stream track results for large result sets.
    /// </summary>
    public async IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tracks = await SearchTracksAsync(query, cancellationToken);
        foreach (var track in tracks)
        {
            yield return track;
        }
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("Disposing MyIndexer");
        return ValueTask.CompletedTask;
    }
}
```

Create `MyStreamingPlugin/MyApiClient.cs`:

```csharp
using System.Net.Http.Json;
using Lidarr.Plugin.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace MyStreamingPlugin;

/// <summary>
/// HTTP client for the streaming service API.
/// Configured via DI with retry policies and timeouts.
/// </summary>
public sealed class MyApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MyApiClient> _logger;
    private readonly MyPluginSettings _settings;

    public MyApiClient(
        HttpClient httpClient,
        ILogger<MyApiClient> logger,
        MyPluginSettings settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings;

        // Add authorization header
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.ApiKey}");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("ping", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"search/albums?q={Uri.EscapeDataString(query)}&region={_settings.Region}";

        if (_settings.EnableDebugLogging)
        {
            _logger.LogDebug("API Request: {Url}", url);
        }

        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<ApiSearchResponse>(
            cancellationToken: cancellationToken);

        return apiResponse?.Albums?.Select(MapToStreamingAlbum).ToList()
               ?? new List<StreamingAlbum>();
    }

    public async Task<IReadOnlyList<StreamingTrack>> SearchTracksAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"search/tracks?q={Uri.EscapeDataString(query)}&region={_settings.Region}";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<ApiSearchResponse>(
            cancellationToken: cancellationToken);

        return apiResponse?.Tracks?.Select(MapToStreamingTrack).ToList()
               ?? new List<StreamingTrack>();
    }

    public async Task<StreamingAlbum?> GetAlbumAsync(
        string albumId,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"albums/{albumId}", cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        var apiAlbum = await response.Content.ReadFromJsonAsync<ApiAlbum>(
            cancellationToken: cancellationToken);

        return apiAlbum != null ? MapToStreamingAlbum(apiAlbum) : null;
    }

    // Map API models to shared library models
    private static StreamingAlbum MapToStreamingAlbum(ApiAlbum api) => new()
    {
        Id = api.Id,
        Title = api.Title,
        ArtistName = api.Artist,
        ReleaseDate = api.ReleaseDate,
        TrackCount = api.TrackCount,
        CoverUrl = api.CoverUrl
    };

    private static StreamingTrack MapToStreamingTrack(ApiTrack api) => new()
    {
        Id = api.Id,
        Title = api.Title,
        ArtistName = api.Artist,
        AlbumName = api.Album,
        DurationMs = api.DurationMs,
        TrackNumber = api.TrackNumber
    };
}

// API response models (internal to your plugin)
internal record ApiSearchResponse(List<ApiAlbum>? Albums, List<ApiTrack>? Tracks);
internal record ApiAlbum(string Id, string Title, string Artist, DateTime? ReleaseDate, int TrackCount, string? CoverUrl);
internal record ApiTrack(string Id, string Title, string Artist, string Album, int DurationMs, int TrackNumber);
```

---

## 6. Adding the Plugin Manifest

Create `MyStreamingPlugin/plugin.json`:

```json
{
  "$schema": "https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/main/plugin.schema.json",
  "id": "mystreamingplugin",
  "name": "My Streaming Plugin",
  "version": "1.0.0",
  "apiVersion": "1.x",
  "commonVersion": "1.2.2",
  "minHostVersion": "2.12.0",
  "entryAssembly": "MyStreamingPlugin.dll",
  "author": "Your Name",
  "description": "Search and download music from My Streaming Service",
  "homepage": "https://github.com/yourname/mystreamingplugin",
  "tags": ["streaming", "music", "lossless"]
}
```

**Field reference**:

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Unique plugin identifier (lowercase, no spaces) |
| `name` | Yes | Display name in Lidarr UI |
| `version` | Yes | Plugin version (SemVer) |
| `apiVersion` | Yes | Abstractions API version (`1.x` for current) |
| `commonVersion` | Yes | Lidarr.Plugin.Common version shipped |
| `minHostVersion` | Yes | Minimum Lidarr version required |
| `entryAssembly` | Yes | Main DLL filename |

---

## 7. Testing Your Plugin

### Build the plugin

```bash
cd MyStreamingPlugin
dotnet build -c Debug
```

### Check the output

Your `bin/Debug/net8.0/` folder should contain:

```
MyStreamingPlugin.dll          # Your plugin
Lidarr.Plugin.Common.dll       # Common library (MUST be present)
plugin.json                    # Manifest
[other dependencies]
```

### Manual testing

1. Copy the entire output folder to Lidarr's plugins directory:
   ```bash
   # Linux/macOS
   cp -r bin/Debug/net8.0/* ~/.config/Lidarr/plugins/MyStreamingPlugin/

   # Windows
   xcopy /E bin\Debug\net8.0\* "%APPDATA%\Lidarr\plugins\MyStreamingPlugin\"
   ```

2. Restart Lidarr

3. Check System > Plugins - your plugin should appear

4. Configure in Settings > Indexers > Add > My Streaming Plugin

### Unit testing

Create `MyStreamingPlugin.Tests/MyIndexerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MyStreamingPlugin.Tests;

public class MyIndexerTests
{
    [Fact]
    public async Task InitializeAsync_WithValidApiKey_ReturnsSuccess()
    {
        // Arrange
        var settings = new MyPluginSettings { ApiKey = "valid-key" };
        var mockClient = new Mock<MyApiClient>();
        mockClient.Setup(c => c.TestConnectionAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);

        var indexer = new MyIndexer(
            NullLogger<MyIndexer>.Instance,
            mockClient.Object,
            settings);

        // Act
        var result = await indexer.InitializeAsync();

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task InitializeAsync_WithEmptyApiKey_ReturnsFailure()
    {
        // Arrange
        var settings = new MyPluginSettings { ApiKey = "" };
        var mockClient = new Mock<MyApiClient>();

        var indexer = new MyIndexer(
            NullLogger<MyIndexer>.Instance,
            mockClient.Object,
            settings);

        // Act
        var result = await indexer.InitializeAsync();

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("API Key", result.Errors.First());
    }
}
```

---

## 8. Packaging for Distribution

### Release build

```bash
dotnet publish -c Release -o ./publish
```

### Create distribution package

```bash
# Create zip for distribution
cd publish
zip -r ../MyStreamingPlugin-1.0.0.zip .
```

### Distribution checklist

- [ ] `MyStreamingPlugin.dll` present
- [ ] `Lidarr.Plugin.Common.dll` present
- [ ] `plugin.json` present and valid
- [ ] All third-party dependencies included
- [ ] No Abstractions DLL in package (host provides it)
- [ ] README.md with setup instructions

---

## 9. Common Issues & Solutions

### Plugin not appearing in Lidarr

| Symptom | Cause | Solution |
|---------|-------|----------|
| Plugin not listed | Missing `plugin.json` | Ensure `plugin.json` is in output (check `.csproj` Copy setting) |
| Plugin not loaded | Version mismatch | Check `minHostVersion` matches your Lidarr version |
| Startup error in logs | Missing Common.dll | Ensure `CopyLocalLockFileAssemblies=true` in `.csproj` |

### Build errors

| Error | Cause | Solution |
|-------|-------|----------|
| `CS0246: IPlugin not found` | Missing Abstractions | Add `PackageReference` to Abstractions |
| `CS0012: Type defined in assembly not referenced` | Missing Common | Add `PackageReference` to Common |
| Type mismatch at runtime | Version conflict | Ensure same Common version in manifest and .csproj |

### Runtime errors

| Error | Cause | Solution |
|-------|-------|----------|
| `InvalidOperationException: Settings is null` | Accessed before init | Only access settings in/after `InitializeAsync` |
| `FileNotFoundException` at plugin load | Missing dependency | Check all NuGet packages are in output folder |
| `ReflectionTypeLoadException` | ABI mismatch | Rebuild with matching Abstractions version |

### Debugging tips

1. **Enable verbose logging** in Lidarr's settings
2. **Check Lidarr logs** at `~/.config/Lidarr/logs/lidarr.txt`
3. **Use `dotnet publish`** instead of `dotnet build` for cleaner output
4. **Test in isolation** with the [IsolationHostSample](../examples/ISOLATION_HOST_SAMPLE.md)

---

## 10. Next Steps

Now that you have a working plugin, extend it with:

### Add OAuth authentication
See [How to: Authenticate with OAuth](../how-to/AUTHENTICATE_OAUTH.md)

### Add a download client
See [How to: Implement a Download Client](../how-to/IMPLEMENT_DOWNLOAD_CLIENT.md)

### Add caching
See [How to: HTTP Caching and Revalidation](../how-to/HTTP_CACHING_AND_REVALIDATION.md)

### Add rate limiting
See the `IUniversalAdaptiveRateLimiter` in [Key Services Reference](../reference/KEY_SERVICES.md)

### Study real plugins
- [Tidalarr](https://github.com/RicherTunes/tidalarr) - Tidal streaming plugin
- [Qobuzarr](https://github.com/RicherTunes/qobuzarr) - Qobuz streaming plugin
- [Brainarr](https://github.com/RicherTunes/brainarr) - AI recommendation plugin

---

## Complete File Structure

```
MyStreamingPlugin/
├── MyStreamingPlugin.sln
├── MyStreamingPlugin/
│   ├── MyStreamingPlugin.csproj
│   ├── plugin.json
│   ├── MyPlugin.cs              # Entry point
│   ├── MyPluginModule.cs        # Metadata
│   ├── MyPluginSettings.cs      # Settings
│   ├── MyIndexer.cs             # Search logic
│   └── MyApiClient.cs           # HTTP client
└── MyStreamingPlugin.Tests/
    ├── MyStreamingPlugin.Tests.csproj
    └── MyIndexerTests.cs
```

---

## Questions?

- Check the [FAQ](../FAQ_FOR_PLUGIN_AUTHORS.md)
- Review [Architecture concepts](../concepts/ARCHITECTURE.md)
- Browse [How-to guides](../how-to/) for specific features
