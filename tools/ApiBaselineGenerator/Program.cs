using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length == 1 && args[0] == "--inspect")
{
    Console.WriteLine("DocId generator ready");
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ApiBaselineGenerator <assemblyPath> <outputPath>");
    return 1;
}

var assemblyPath = Path.GetFullPath(args[0]);
var outputPath   = Path.GetFullPath(args[1]);

if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"Assembly not found: {assemblyPath}");
    return 2;
}

var references = new List<MetadataReference>();
string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
if (runtimeDir is not null)
{
    foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
    {
        try
        {
            references.Add(MetadataReference.CreateFromFile(dll));
        }
        catch
        {
        }
    }
}

references.Add(MetadataReference.CreateFromFile(assemblyPath));

var compilation = CSharpCompilation.Create(
    assemblyName: "ApiBaselineGenerator",
    syntaxTrees: Array.Empty<SyntaxTree>(),
    references: references,
    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(references.Last()) as IAssemblySymbol;
if (assemblySymbol is null)
{
    Console.Error.WriteLine("Unable to load assembly symbol.");
    return 3;
}

var docIds = new SortedSet<string>(StringComparer.Ordinal);
AddNamespace(assemblySymbol.GlobalNamespace, docIds);

var lines = new List<string> { "#nullable enable", string.Empty };
lines.AddRange(docIds);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllLines(outputPath, lines);
return 0;

static void AddNamespace(INamespaceSymbol namespaceSymbol, SortedSet<string> docIds)
{
    if (!namespaceSymbol.IsGlobalNamespace)
    {
        AddSymbol(namespaceSymbol, docIds);
    }

    foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
    {
        AddNamespace(nestedNamespace, docIds);
    }

    foreach (var type in namespaceSymbol.GetTypeMembers())
    {
        AddType(type, docIds);
    }
}

static void AddType(INamedTypeSymbol typeSymbol, SortedSet<string> docIds)
{
    if (!IsPubliclyAccessible(typeSymbol))
    {
        return;
    }

    AddSymbol(typeSymbol, docIds);

    foreach (var member in typeSymbol.GetMembers())
    {
        if (member is INamedTypeSymbol nestedType)
        {
            AddType(nestedType, docIds);
        }
        else
        {
            AddMember(member, docIds);
        }
    }
}

static void AddMember(ISymbol memberSymbol, SortedSet<string> docIds)
{
    if (!IsPubliclyAccessible(memberSymbol))
    {
        return;
    }

    AddSymbol(memberSymbol, docIds);
}

static void AddSymbol(ISymbol symbol, SortedSet<string> docIds)
{
    var id = DocumentationCommentId.CreateDeclarationId(symbol);
    if (!string.IsNullOrWhiteSpace(id))
    {
        docIds.Add(id!);
    }
}

static bool IsPubliclyAccessible(ISymbol symbol)
{
    return symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => true,
        Accessibility.Protected => true,
        Accessibility.ProtectedOrInternal => true,
        _ => false
    };
}
