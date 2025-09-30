using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PublicApiBaseliner;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: PublicApiBaseliner <assembly-path> <output-path>");
            return 1;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);

        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"Assembly not found: {assemblyPath}");
            return 1;
        }

        try
        {
            var docIds = GeneratePublicApiDocIds(assemblyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            using var writer = new StreamWriter(outputPath, append: false);
            writer.WriteLine("#nullable enable");
            writer.WriteLine();
            foreach (var id in docIds)
            {
                writer.WriteLine(id);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static SortedSet<string> GeneratePublicApiDocIds(string assemblyPath)
    {
        var (references, targetReference) = BuildMetadataReferences(assemblyPath);
        var compilation = CSharpCompilation.Create(
            assemblyName: "PublicApiSnapshot",
            syntaxTrees: Array.Empty<SyntaxTree>(),
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        if (compilation.GetAssemblyOrModuleSymbol(targetReference) is not IAssemblySymbol assemblySymbol)
        {
            throw new InvalidOperationException($"Unable to load symbols for '{assemblyPath}'.");
        }

        var docIds = new SortedSet<string>(DocIdComparer.Instance);
        CollectNamespaceSymbols(assemblySymbol.GlobalNamespace, docIds);
        return docIds;
    }

    private static (IReadOnlyList<MetadataReference> References, PortableExecutableReference TargetReference) BuildMetadataReferences(string assemblyPath)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpaList)
        {
            foreach (var path in tpaList.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path))
                {
                    continue;
                }

                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
        foreach (var dll in Directory.GetFiles(assemblyDirectory, "*.dll"))
        {
            if (string.Equals(dll, assemblyPath, StringComparison.OrdinalIgnoreCase) || !seen.Add(dll))
            {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(dll));
        }

        var targetReference = MetadataReference.CreateFromFile(assemblyPath);
        references.Add(targetReference);
        return (references, targetReference);
    }

    private static void CollectNamespaceSymbols(INamespaceSymbol namespaceSymbol, SortedSet<string> docIds)

    {

        foreach (var member in namespaceSymbol.GetMembers())

        {

            switch (member)

            {

                case INamespaceSymbol nestedNamespace:

                    CollectNamespaceSymbols(nestedNamespace, docIds);

                    break;

                case INamedTypeSymbol typeSymbol:

                    CollectTypeSymbols(typeSymbol, docIds);

                    break;

            }

        }

    }

    private static void CollectTypeSymbols(INamedTypeSymbol typeSymbol, SortedSet<string> docIds)

    {

        if (!IsAccessible(typeSymbol.DeclaredAccessibility) || typeSymbol.IsImplicitlyDeclared)

        {

            return;

        }

        var typeId = DocumentationCommentId.CreateDeclarationId(typeSymbol);

        if (string.IsNullOrEmpty(typeId))

        {

            Console.Error.WriteLine($"[PublicApiBaseliner] Warning: unable to compute doc ID for type '{typeSymbol.ToDisplayString()}'.");

        }

        else

        {

            docIds.Add(typeId);

        }

        foreach (var member in typeSymbol.GetMembers())

        {

            switch (member)

            {

                case INamedTypeSymbol nestedTypeSymbol:

                    CollectTypeSymbols(nestedTypeSymbol, docIds);

                    break;

                case IMethodSymbol methodSymbol when ShouldInclude(methodSymbol):

                    AddDocumentationId(methodSymbol, docIds);

                    break;

                case IPropertySymbol propertySymbol when ShouldInclude(propertySymbol):

                    AddDocumentationId(propertySymbol, docIds);

                    break;

                case IEventSymbol eventSymbol when ShouldInclude(eventSymbol):

                    AddDocumentationId(eventSymbol, docIds);

                    break;

                case IFieldSymbol fieldSymbol when ShouldInclude(fieldSymbol):

                    AddDocumentationId(fieldSymbol, docIds);

                    break;

            }

        }

    }

    private static bool ShouldInclude(IMethodSymbol method)
    {
        if (!IsAccessible(method.DeclaredAccessibility) || method.IsImplicitlyDeclared)
        {
            return false;
        }

        return method.MethodKind switch
        {
            MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove => false,
            MethodKind.StaticConstructor => false,
            _ => true,
        };
    }

    private static bool ShouldInclude(IPropertySymbol property)
    {
        if (!IsAccessible(property.DeclaredAccessibility) || property.IsImplicitlyDeclared)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldInclude(IEventSymbol @event)
    {
        if (!IsAccessible(@event.DeclaredAccessibility) || @event.IsImplicitlyDeclared)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldInclude(IFieldSymbol field)
    {
        if (!IsAccessible(field.DeclaredAccessibility) || field.IsImplicitlyDeclared)
        {
            return false;
        }

        return true;
    }

    private static bool IsAccessible(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal or Accessibility.ProtectedAndInternal;
    }

    private static void AddDocumentationId(ISymbol symbol, SortedSet<string> docIds)
    {
        var id = DocumentationCommentId.CreateDeclarationId(symbol);
        if (!string.IsNullOrEmpty(id))
        {
            docIds.Add(id);
        }
    }

    private sealed class DocIdComparer : IComparer<string>
    {
        public static DocIdComparer Instance { get; } = new DocIdComparer();

        private DocIdComparer()
        {
        }

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var categoryComparison = GetCategoryRank(x).CompareTo(GetCategoryRank(y));
            if (categoryComparison != 0)
            {
                return categoryComparison;
            }

            return string.CompareOrdinal(x, y);
        }

        private static int GetCategoryRank(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return int.MaxValue;
            }

            return id[0] switch
            {
                'N' => -1,
                'T' => 0,
                'F' => 1,
                'P' => 2,
                'E' => 3,
                'M' => 4,
                _ => 5,
            };
        }
    }

}

