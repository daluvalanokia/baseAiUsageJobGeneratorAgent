using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SmartAgent.Domain;

namespace SmartAgent.Core.Pm;

/// <summary>One C# class captured by the Roslyn reader: real methods with real parameters.</summary>
public sealed record RoslynClass
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required string Kind { get; init; }               // Controller / Hub / Service / Entity / Component / Other
    /// <summary>Base-list types (e.g. "ControllerBase", "Hub") — evidence for Kind.</summary>
    public IReadOnlyList<string> Bases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
    /// <summary>Method → exact parameter list, "name (Type)" (attributes and defaults stripped).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MethodParams { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>One C# property captured by the Roslyn reader, with its validation attributes.</summary>
public sealed record RoslynProperty
{
    public required string Name { get; init; }
    /// <summary>Type exactly as written in source (e.g. "string", "string?", "int").</summary>
    public required string Type { get; init; }
    /// <summary>Declared [StringLength]/[MaxLength] value, when present.</summary>
    public int? DeclaredSize { get; init; }
    public bool RequiredAttr { get; init; }
    public bool KeyAttr { get; init; }
}

/// <summary>One C# entity class with its properties.</summary>
public sealed record RoslynEntity
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public IReadOnlyList<RoslynProperty> Properties { get; init; } = Array.Empty<RoslynProperty>();
}

/// <summary>
/// Compiler-grade extraction of classes, method parameters and entity properties
/// from a source snapshot using Roslyn syntax trees (parse-only — no build, no
/// references — so it works on any C# file set). Primary extractor for .cs files;
/// the regexes remain the fallback for non-C# languages.
/// </summary>
public static class RoslynReader
{
    /// <summary>All classes/records in the .cs files with exact method signatures.</summary>
    public static List<RoslynClass> ReadClasses(IReadOnlyList<SourceFile> files)
    {
        var result = new List<RoslynClass>();
        foreach (var file in files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var root = Parse(file.Content);
            foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = decl.Identifier.Text;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var bases = decl.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new List<string>();
                var methods = new List<string>();
                var methodParams = new Dictionary<string, IReadOnlyList<string>>();
                foreach (var m in decl.Members.OfType<MethodDeclarationSyntax>()
                             .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)
                                         && m.ExplicitInterfaceSpecifier is null))
                {
                    if (methods.Contains(m.Identifier.Text)) continue;
                    methods.Add(m.Identifier.Text);
                    methodParams[m.Identifier.Text] = ParamsOf(m.ParameterList);
                }

                var surface = methods.Take(8).ToList();
                result.Add(new RoslynClass
                {
                    Name = name, File = file.Path,
                    Kind = KindOf(name, file.Path, bases), Bases = bases,
                    Methods = surface,
                    MethodParams = surface.ToDictionary(
                        m => m, m => methodParams.TryGetValue(m, out var p) ? p : Array.Empty<string>())
                });
            }
        }
        return result;
    }

    /// <summary>All classes with properties in the .cs files, with their validation attributes.</summary>
    public static List<RoslynEntity> ReadEntities(IReadOnlyList<SourceFile> files)
    {
        var result = new List<RoslynEntity>();
        foreach (var file in files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var root = Parse(file.Content);
            foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var props = decl.Members.OfType<PropertyDeclarationSyntax>().Select(PropOf).ToList();
                if (props.Count == 0) continue;
                result.Add(new RoslynEntity
                {
                    Name = decl.Identifier.Text, File = file.Path, Properties = props
                });
            }
        }
        return result;
    }

    // ─── helpers ───

    private static CompilationUnitSyntax Parse(string content)
    {
        try { return CSharpSyntaxTree.ParseText(content).GetCompilationUnitRoot(); }
        catch { return SyntaxFactory.CompilationUnit(); }
    }

    private static IReadOnlyList<string> ParamsOf(ParameterListSyntax list)
    {
        var result = new List<string>();
        foreach (var p in list.Parameters)
        {
            var type = p.Type?.ToString();
            if (string.IsNullOrWhiteSpace(type)) continue;
            result.Add($"{p.Identifier.Text} ({type})");
        }
        return result;
    }

    private static RoslynProperty PropOf(PropertyDeclarationSyntax p) => new()
    {
        Name = p.Identifier.Text,
        Type = p.Type.ToString(),
        DeclaredSize = IntAttributeArg(p.AttributeLists, "StringLength", "MaxLength"),
        RequiredAttr = HasAttribute(p.AttributeLists, "Required"),
        KeyAttr = HasAttribute(p.AttributeLists, "Key")
    };

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Any(a =>
            a.Name.ToString().EndsWith(name, StringComparison.OrdinalIgnoreCase));

    private static int? IntAttributeArg(SyntaxList<AttributeListSyntax> lists, params string[] names)
    {
        foreach (var a in lists.SelectMany(l => l.Attributes))
        {
            if (!names.Any(n => a.Name.ToString().EndsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
            if (a.ArgumentList?.Arguments.FirstOrDefault()?.Expression
                    is LiteralExpressionSyntax { Token.Value: int i }) return i;
        }
        return null;
    }

    /// <summary>Kind from the base list first (compiler truth), then the legacy path heuristics.</summary>
    private static string KindOf(string className, string path, IReadOnlyList<string> bases)
    {
        var basesFlat = string.Join(",", bases);
        if (basesFlat.Contains("Controller")) return "Controller";   // covers ControllerBase too
        if (basesFlat.Contains("Hub")) return "Hub";
        return SourceAnalyzer.KindOfPublic(className, path);
    }
}
