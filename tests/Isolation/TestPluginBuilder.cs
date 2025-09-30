using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;

namespace Lidarr.Plugin.Common.Tests.Isolation
{
    internal sealed class TestPluginBuilder : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _pluginDirectories = new();
        private readonly MetadataReference[] _platformReferences;
        private readonly string _template;

        public TestPluginBuilder()
        {
            _root = Path.Combine(Path.GetTempPath(), $"lidarr-plugin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            _platformReferences = CreatePlatformReferences();
            _template = File.ReadAllText(ResolveTemplatePath());
        }

        public string BuildPlugin(string pluginId, string commonVersion, string? apiVersion = null, string? pluginVersion = null)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                throw new ArgumentException("pluginId must be provided", nameof(pluginId));
            }

            if (string.IsNullOrWhiteSpace(commonVersion))
            {
                throw new ArgumentException("commonVersion must be provided", nameof(commonVersion));
            }

            apiVersion ??= $"{(typeof(IPlugin).Assembly.GetName().Version?.Major ?? 1)}.x";
            pluginVersion ??= "2.3.0";

            var pluginDirectory = Path.Combine(_root, $"{pluginId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(pluginDirectory);
            _pluginDirectories.Add(pluginDirectory);

            var normalizedId = pluginId.ToLowerInvariant();
            var assemblyName = pluginId;
            var assemblyFileName = $"{assemblyName}.dll";
            var pluginAssemblyPath = Path.Combine(pluginDirectory, assemblyFileName);

            var commonAssemblyPath = Path.Combine(pluginDirectory, "Lidarr.Plugin.Common.dll");
            CompileCommonAssembly(commonAssemblyPath, commonVersion);

            var source = ApplyTemplate(pluginId, normalizedId, pluginVersion, apiVersion, commonVersion, assemblyFileName);
            CompilePluginAssembly(assemblyName, source, pluginAssemblyPath, commonAssemblyPath);

            var manifest = new PluginManifest
            {
                Id = normalizedId,
                Name = pluginId,
                Version = pluginVersion,
                ApiVersion = apiVersion,
                CommonVersion = commonVersion,
                MinHostVersion = "2.12.0",
                EntryAssembly = assemblyFileName,
                Capabilities = new[] { "Search" },
                RequiredSettings = new[] { "BaseUrl" }
            };

            File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), manifest.ToJson());

            return pluginDirectory;
        }

        private string ApplyTemplate(string pluginId, string normalizedId, string pluginVersion, string apiVersion, string commonVersion, string assemblyFileName)
        {
            return _template
                .Replace("{{PLUGIN_ID}}", pluginId, StringComparison.Ordinal)
                .Replace("{{LOWER_ID}}", normalizedId, StringComparison.Ordinal)
                .Replace("{{PLUGIN_VERSION}}", pluginVersion, StringComparison.Ordinal)
                .Replace("{{API_VERSION}}", apiVersion, StringComparison.Ordinal)
                .Replace("{{COMMON_VERSION}}", commonVersion, StringComparison.Ordinal)
                .Replace("{{ASSEMBLY_NAME}}", assemblyFileName, StringComparison.Ordinal);
        }

        private void CompileCommonAssembly(string outputPath, string version)
        {
            var assemblyVersion = ToAssemblyVersion(version);
            var source = $@"
using System.Reflection;

[assembly: AssemblyVersion(""{assemblyVersion}"")]
[assembly: AssemblyFileVersion(""{assemblyVersion}"")]
[assembly: AssemblyInformationalVersion(""{version}"")]

namespace Lidarr.Plugin.Common
{{
    public static class CommonVersion
    {{
        public static string Value {{ get; }} = ""{version}"";
    }}
}}
";

            var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
            var compilation = CSharpCompilation.Create(
                "Lidarr.Plugin.Common",
                new[] { syntaxTree },
                _platformReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Emit(compilation, outputPath);
        }

        private void CompilePluginAssembly(string assemblyName, string source, string outputPath, string commonAssemblyPath)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

            var references = new List<MetadataReference>(_platformReferences.Length + 3);
            foreach (var reference in _platformReferences)
            {
                if (!IsAssembly(reference, "Lidarr.Plugin.Common.dll"))
                {
                    references.Add(reference);
                }
            }

            references.Add(MetadataReference.CreateFromFile(typeof(IPlugin).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(PluginManifest).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(commonAssemblyPath));

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Emit(compilation, outputPath);
        }

        private static bool IsAssembly(MetadataReference reference, string assemblyFileName)
        {
            if (reference is PortableExecutableReference portable && portable.FilePath is not null)
            {
                return string.Equals(Path.GetFileName(portable.FilePath), assemblyFileName, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrEmpty(reference.Display))
            {
                return string.Equals(Path.GetFileName(reference.Display), assemblyFileName, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static void Emit(CSharpCompilation compilation, string outputPath)
        {
            using var stream = File.Create(outputPath);
            var result = compilation.Emit(stream);

            if (!result.Success)
            {
                var diagnostics = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
                throw new InvalidOperationException($"Compilation failed:{Environment.NewLine}{diagnostics}");
            }
        }

        private static MetadataReference[] CreatePlatformReferences()
        {
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrEmpty(tpa))
            {
                throw new InvalidOperationException("Unable to locate trusted platform assemblies for Roslyn compilation.");
            }

            var references = new List<MetadataReference>();
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var reference = MetadataReference.CreateFromFile(path);
                if (!IsAssembly(reference, "Lidarr.Plugin.Common.dll"))
                {
                    references.Add(reference);
                }
            }

            return references.ToArray();
        }

        private static string ResolveTemplatePath()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Isolation", "Templates", "PluginTemplate.cs.txt"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Isolation", "Templates", "PluginTemplate.cs.txt"))
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Unable to locate PluginTemplate.cs.txt for test plugin generation.");
        }

        private static string ToAssemblyVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return "1.0.0.0";
            }

            var sanitized = version.Split('+')[0];
            var dashIndex = sanitized.IndexOf('-');
            if (dashIndex >= 0)
            {
                sanitized = sanitized[..dashIndex];
            }

            var parts = sanitized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var values = new int[4];

            for (var i = 0; i < values.Length; i++)
            {
                if (i < parts.Length && int.TryParse(parts[i], out var parsed))
                {
                    values[i] = parsed;
                }
                else
                {
                    values[i] = 0;
                }
            }

            return $"{values[0]}.{values[1]}.{values[2]}.{values[3]}";
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup for temporary test plugins
            }
        }
    }
}
