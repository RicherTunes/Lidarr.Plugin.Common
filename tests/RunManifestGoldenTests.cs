using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Golden fixture tests for run-manifest.json validation.
    /// These tests validate that our fixture files conform to the schema
    /// and that key fields contain expected values for each scenario.
    /// </summary>
    public class RunManifestGoldenTests
    {
        private static readonly string FixturesPath = Path.Combine(
            AppContext.BaseDirectory, "fixtures", "run-manifests");

        private static readonly string SchemaPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "reference", "e2e-run-manifest.schema.json");

        private static readonly Lazy<JsonSchema> Schema = new(() =>
        {
            var schemaText = File.ReadAllText(SchemaPath);
            return JsonSchema.FromText(schemaText);
        });

        public static IEnumerable<object[]> GoldenFixtures =>
            Directory.GetFiles(FixturesPath, "*.json")
                .Select(f => new object[] { Path.GetFileNameWithoutExtension(f), f });

        [Theory]
        [MemberData(nameof(GoldenFixtures))]
        public void Fixture_ValidatesAgainstSchema(string fixtureName, string fixturePath)
        {
            var json = File.ReadAllText(fixturePath);
            var node = JsonNode.Parse(json);

            var result = Schema.Value.Evaluate(node, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });

            Assert.True(result.IsValid,
                $"Fixture '{fixtureName}' failed schema validation:\n" +
                string.Join("\n", result.Details
                    .Where(d => !d.IsValid && d.Errors != null)
                    .SelectMany(d => d.Errors!.Select(e => $"  {d.InstanceLocation}: {e.Key} - {e.Value}"))));
        }

        [Fact]
        public void PassBaseline_HasExpectedStructure()
        {
            var manifest = LoadFixture("pass-baseline");

            Assert.True(manifest.GetProperty("summary").GetProperty("overallSuccess").GetBoolean());
            Assert.Equal(2, manifest.GetProperty("summary").GetProperty("passed").GetInt32());
            Assert.Equal(0, manifest.GetProperty("summary").GetProperty("failed").GetInt32());
            Assert.False(manifest.GetProperty("hostBugSuspected").GetProperty("detected").GetBoolean());
        }

        [Fact]
        public void AuthMissing_HasCorrectErrorCode()
        {
            var manifest = LoadFixture("auth-missing");

            Assert.False(manifest.GetProperty("summary").GetProperty("overallSuccess").GetBoolean());

            var results = manifest.GetProperty("results").EnumerateArray().ToList();
            var failedGate = results.First(r => r.GetProperty("outcome").GetString() == "failed");

            Assert.Equal("E2E_AUTH_MISSING", failedGate.GetProperty("errorCode").GetString());
            Assert.Equal("Search", failedGate.GetProperty("gate").GetString());
        }

        [Fact]
        public void ComponentAmbiguous_HasUnsafeResolution()
        {
            var manifest = LoadFixture("component-ambiguous");

            Assert.True(manifest.GetProperty("summary").GetProperty("overallSuccess").GetBoolean());

            var results = manifest.GetProperty("results").EnumerateArray().ToList();
            var configureGate = results.First(r => r.GetProperty("gate").GetString() == "Configure");
            var resolution = configureGate.GetProperty("details").GetProperty("componentResolution");
            var indexer = resolution.GetProperty("indexer");

            Assert.False(indexer.GetProperty("safeToPersist").GetBoolean());
            Assert.Equal("none", indexer.GetProperty("matchedOn").GetString());
            Assert.True(indexer.GetProperty("candidateIds").GetArrayLength() > 1);
        }

        [Fact]
        public void HostBugAlc_HasCorrectClassification()
        {
            var manifest = LoadFixture("host-bug-alc");

            Assert.False(manifest.GetProperty("summary").GetProperty("overallSuccess").GetBoolean());

            var hostBug = manifest.GetProperty("hostBugSuspected");
            Assert.True(hostBug.GetProperty("detected").GetBoolean());
            Assert.Equal("ALC", hostBug.GetProperty("classification").GetString());
            Assert.Equal("host_bug", hostBug.GetProperty("severity").GetString());
        }

        [Fact]
        public void DiscoveryDisabled_HasCorrectClassification()
        {
            var manifest = LoadFixture("discovery-disabled");

            Assert.False(manifest.GetProperty("summary").GetProperty("overallSuccess").GetBoolean());

            var hostBug = manifest.GetProperty("hostBugSuspected");
            Assert.True(hostBug.GetProperty("detected").GetBoolean());
            Assert.Equal("DISCOVERY_DISABLED", hostBug.GetProperty("classification").GetString());

            var results = manifest.GetProperty("results").EnumerateArray().ToList();
            var failedGate = results.First(r => r.GetProperty("outcome").GetString() == "failed");
            Assert.Equal("E2E_HOST_PLUGIN_DISCOVERY_DISABLED", failedGate.GetProperty("errorCode").GetString());
        }

        [Theory]
        [InlineData("pass-baseline", true)]
        [InlineData("auth-missing", false)]
        [InlineData("component-ambiguous", true)]
        [InlineData("host-bug-alc", false)]
        [InlineData("discovery-disabled", false)]
        public void Fixture_OverallSuccessMatchesExpectation(string fixtureName, bool expectedSuccess)
        {
            var manifest = LoadFixture(fixtureName);
            Assert.Equal(expectedSuccess, manifest.GetProperty("summary").GetProperty("overallSuccess").GetBoolean());
        }

        [Theory]
        [InlineData("pass-baseline", false)]
        [InlineData("auth-missing", false)]
        [InlineData("component-ambiguous", false)]
        [InlineData("host-bug-alc", true)]
        [InlineData("discovery-disabled", true)]
        public void Fixture_HostBugDetectedMatchesExpectation(string fixtureName, bool expectedDetected)
        {
            var manifest = LoadFixture(fixtureName);
            Assert.Equal(expectedDetected, manifest.GetProperty("hostBugSuspected").GetProperty("detected").GetBoolean());
        }

        private static JsonElement LoadFixture(string name)
        {
            var path = Path.Combine(FixturesPath, $"{name}.json");
            var json = File.ReadAllText(path);
            return JsonDocument.Parse(json).RootElement;
        }
    }
}
