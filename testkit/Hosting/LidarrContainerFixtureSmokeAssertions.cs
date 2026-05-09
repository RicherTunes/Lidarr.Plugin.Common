using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Hosting;

/// <summary>
/// Standard Docker E2E smoke assertions for any streaming plugin loaded into a
/// real Lidarr container. Wave 22a — extension methods over
/// <see cref="LidarrContainerFixture"/> so each plugin's E2E test class is just
/// a thin wrapper that constructs <see cref="LidarrContainerOptions"/> and
/// delegates to these.
/// </summary>
public static class LidarrContainerFixtureSmokeAssertions
{
    /// <summary>
    /// Asserts <c>GET /api/v1/indexer/schema</c> contains an entry whose
    /// <c>name</c> or <c>implementation</c> matches the plugin's
    /// <see cref="LidarrContainerOptions.PluginEntrySubstring"/>.
    /// </summary>
    public static async Task AssertPluginAppearsInIndexerSchemaAsync(this LidarrContainerFixture fixture)
        => await AssertSchemaContainsPluginAsync(fixture, "indexer").ConfigureAwait(false);

    /// <summary>
    /// Asserts <c>GET /api/v1/downloadclient/schema</c> contains an entry whose
    /// <c>name</c> or <c>implementation</c> matches the plugin's
    /// <see cref="LidarrContainerOptions.PluginEntrySubstring"/>.
    /// </summary>
    public static async Task AssertPluginAppearsInDownloadClientSchemaAsync(this LidarrContainerFixture fixture)
        => await AssertSchemaContainsPluginAsync(fixture, "downloadclient").ConfigureAwait(false);

    /// <summary>
    /// POSTs the plugin's indexer schema entry back to <c>/api/v1/indexer/test</c>
    /// and asserts the response is &lt; 500. With no real credentials a 4xx
    /// validation failure is expected; a 500 means the plugin failed to load.
    /// </summary>
    public static async Task AssertIndexerTestReturnsSensibleFailureAsync(this LidarrContainerFixture fixture)
        => await AssertTestEndpointReturnsValidationFailureAsync(fixture, "indexer").ConfigureAwait(false);

    /// <summary>
    /// POSTs the plugin's downloadclient schema entry back to
    /// <c>/api/v1/downloadclient/test</c> and asserts the response is &lt; 500.
    /// </summary>
    public static async Task AssertDownloadClientTestReturnsSensibleFailureAsync(this LidarrContainerFixture fixture)
        => await AssertTestEndpointReturnsValidationFailureAsync(fixture, "downloadclient").ConfigureAwait(false);

    // -- helpers -----------------------------------------------------------

    private static async Task AssertSchemaContainsPluginAsync(LidarrContainerFixture fixture, string kind)
    {
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));

        string url = $"{fixture.BaseUrl}/api/v1/{kind}/schema?apikey={fixture.ApiKey}";
        string json = await fixture.Http.GetStringAsync(url).ConfigureAwait(false);

        Assert.True(SchemaContainsPlugin(json, fixture.Options.PluginEntrySubstring),
            $"Expected {kind} schema to include a '{fixture.Options.PluginEntrySubstring}' entry. " +
            $"Logs:\n{Truncate(fixture.GetContainerLogs(), 2000)}\n\nSchema:\n{Truncate(json, 1500)}");
    }

    private static async Task AssertTestEndpointReturnsValidationFailureAsync(LidarrContainerFixture fixture, string kind)
    {
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));

        JsonElement? schemaEntry = await GetPluginSchemaEntryAsync(fixture, kind).ConfigureAwait(false);
        if (schemaEntry is null)
        {
            // Graceful skip: if the schema entry isn't there, the
            // schema-load smoke test will already have failed louder.
            Skip.If(true, $"No '{fixture.Options.PluginEntrySubstring}' {kind} schema entry — plugin likely not loaded.");
            return;
        }

        string testUrl = $"{fixture.BaseUrl}/api/v1/{kind}/test?apikey={fixture.ApiKey}";
        using StringContent content = new(schemaEntry.Value.GetRawText(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await fixture.Http.PostAsync(testUrl, content).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Acceptance: NOT 500. Real plugin-load failures show up as 500.
        Assert.True(
            (int)response.StatusCode < 500,
            $"Expected non-5xx from /{kind}/test (plugin-load smoke), got {(int)response.StatusCode} {response.StatusCode}.\n" +
            $"Body: {Truncate(body, 1500)}\n" +
            $"Logs:\n{Truncate(fixture.GetContainerLogs(), 1500)}");
    }

    private static async Task<JsonElement?> GetPluginSchemaEntryAsync(LidarrContainerFixture fixture, string kind)
    {
        string schemaUrl = $"{fixture.BaseUrl}/api/v1/{kind}/schema?apikey={fixture.ApiKey}";
        string json = await fixture.Http.GetStringAsync(schemaUrl).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);

        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            if (EntryMatchesPlugin(entry, fixture.Options.PluginEntrySubstring))
            {
                // Clone so the parent JsonDocument can be disposed
                return JsonDocument.Parse(entry.GetRawText()).RootElement;
            }
        }

        return null;
    }

    private static bool SchemaContainsPlugin(string schemaJson, string substring)
    {
        using JsonDocument doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.EnumerateArray().Any(e => EntryMatchesPlugin(e, substring));
    }

    private static bool EntryMatchesPlugin(JsonElement entry, string substring)
    {
        string name = entry.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        string impl = entry.TryGetProperty("implementation", out JsonElement i) ? i.GetString() ?? "" : "";
        return name.Contains(substring, StringComparison.OrdinalIgnoreCase)
            || impl.Contains(substring, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "... (truncated)";
}
