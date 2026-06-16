/// <summary>
/// Roslyn-based extractor for VB.NET and C# files.
/// Outputs JSON { nodes: [...], edges: [...] } to stdout.
///
/// Usage:
///   roslyn-extractor <file.vb>   -- extract single file
///   roslyn-extractor <file.cs>   -- extract single file
/// </summary>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// ── Entry point (top-level statements MUST precede type declarations) ────────

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: roslyn-extractor <file.vb|file.cs>");
    Environment.Exit(1);
}

var filePath = args[0];
if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"File not found: {filePath}");
    Environment.Exit(1);
}

GraphResult result;
var ext = Path.GetExtension(filePath).ToLowerInvariant();

if (ext == ".vb")
    result = VBExtractor.Extract(filePath);
else if (ext == ".cs")
    result = CSharpExtractor.Extract(filePath);
else
{
    Console.Error.WriteLine($"Unsupported extension: {ext}");
    Environment.Exit(1);
    return;
}

var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = false,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
});
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine(json);

// ── JSON models ──────────────────────────────────────────────────────────────

record GraphNode(
    [property: JsonPropertyName("id")]              string Id,
    [property: JsonPropertyName("label")]           string Label,
    [property: JsonPropertyName("file_type")]       string FileType,
    [property: JsonPropertyName("source_file")]     string SourceFile,
    [property: JsonPropertyName("source_location")] string SourceLocation,
    [property: JsonPropertyName("node_type")]       string NodeType
);

record GraphEdge(
    [property: JsonPropertyName("source")]          string Source,
    [property: JsonPropertyName("target")]          string Target,
    [property: JsonPropertyName("relation")]        string Relation,
    [property: JsonPropertyName("confidence")]      string Confidence,
    [property: JsonPropertyName("source_file")]     string SourceFile,
    [property: JsonPropertyName("source_location")] string SourceLocation,
    [property: JsonPropertyName("weight")]          double Weight
);

record GraphResult(
    [property: JsonPropertyName("nodes")] List<GraphNode> Nodes,
    [property: JsonPropertyName("edges")] List<GraphEdge> Edges
);

// ── Shared helpers ───────────────────────────────────────────────────────────

static class Helpers
{
    static readonly HashSet<string> VBKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if","else","end","for","while","do","select","case",
        "try","catch","finally","return","throw","new","nothing",
        "true","false","me","mybase","myclass","dim","with",
        "each","next","loop","then","to","step","exit","continue",
        "as","is","in","not","and","or","xor","get","set","add","remove",
    };

    public static bool IsVBKeyword(string name) => VBKeywords.Contains(name);

    /// <summary>
    /// Locate System.Private.CoreLib.dll — works for normal, self-contained,
    /// AND single-file deployments (where Assembly.Location returns "").
    /// </summary>
    public static string ResolveCorLib()
    {
        var loc = typeof(object).Assembly.Location;
        if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
            return loc;

        var candidates = new List<string>();

        // Single-file: files extracted to AppContext.BaseDirectory temp dir
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "System.Private.CoreLib.dll"));
        candidates.Add(Path.Combine(baseDir, "mscorlib.dll"));

        // Exe directory
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (!string.IsNullOrEmpty(exeDir))
        {
            candidates.Add(Path.Combine(exeDir, "System.Private.CoreLib.dll"));
            candidates.Add(Path.Combine(exeDir, "mscorlib.dll"));
        }

        // .NET runtime dir (compat shim)
        try
        {
            var rtDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            if (!string.IsNullOrEmpty(rtDir))
            {
                candidates.Add(Path.Combine(rtDir, "System.Private.CoreLib.dll"));
                candidates.Add(Path.Combine(rtDir, "mscorlib.dll"));
            }
        }
        catch { }

        // Walk up from exe to find shared/Microsoft.NETCore.App/<ver>/
        try
        {
            var dir = exeDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                var shared = Path.Combine(dir, "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(shared))
                {
                    foreach (var ver in Directory.GetDirectories(shared)
                                                  .OrderByDescending(d => d))
                    {
                        var p = Path.Combine(ver, "System.Private.CoreLib.dll");
                        if (File.Exists(p)) return p;
                    }
                }
                dir = Path.GetDirectoryName(dir) ?? "";
            }
        }
        catch { }

        // DOTNET_ROOT env var
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "";
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var shared = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
            if (Directory.Exists(shared))
                foreach (var ver in Directory.GetDirectories(shared)
                                              .OrderByDescending(d => d))
                {
                    var p = Path.Combine(ver, "System.Private.CoreLib.dll");
                    if (File.Exists(p)) return p;
                }
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found != null) return found;

        throw new InvalidOperationException(
            "Cannot locate System.Private.CoreLib.dll. " +
            "Set DOTNET_ROOT or use a framework-dependent deployment.");
    }

    /// <summary>Graphify-compatible node ID: lowercase, dots/slashes/spaces → underscores.</summary>
    public static string MakeId(params string[] parts)
    {
        var joined = string.Join(".", parts.Where(p => !string.IsNullOrEmpty(p)));
        return joined
            .ToLowerInvariant()
            .Replace(" ", "_")
            .Replace("\\", "_")
            .Replace("/", "_")
            .Replace(":", "_");
    }

    public static string LineRef(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"L{span.StartLinePosition.Line + 1}";
    }

    public static string LineRef(int oneBased) => $"L{oneBased}";
}

// ── VB.NET extractor ─────────────────────────────────────────────────────────

static class VBExtractor
{
    public static GraphResult Extract(string filePath)
    {
        var source = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
        var syntaxTree = VisualBasicSyntaxTree.ParseText(
            SourceText.From(source, System.Text.Encoding.UTF8),
            path: filePath);

        // Semantic compilation (single-file; gives full type resolution within file)
        var mscorlib = MetadataReference.CreateFromFile(Helpers.ResolveCorLib());
        var compilation = VisualBasicCompilation.Create(
            "RoslynExtract",
            syntaxTrees: new[] { syntaxTree },
            references: new[] { mscorlib },
            options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(syntaxTree);
        var root  = syntaxTree.GetRoot();

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var seen  = new HashSet<string>();

        var strPath = filePath;
        var stem    = Path.GetFileNameWithoutExtension(filePath);
        var fileNid = Helpers.MakeId(strPath);

        void AddNode(string nid, string label, string nodeType, string lineRef)
        {
            if (seen.Add(nid))
                nodes.Add(new GraphNode(nid, label, "code", strPath, lineRef, nodeType));
        }

        void AddEdge(string src, string tgt, string relation, string lineRef,
                     string confidence = "EXTRACTED")
        {
            edges.Add(new GraphEdge(src, tgt, relation, confidence, strPath, lineRef, 1.0));
        }

        AddNode(fileNid, Path.GetFileName(filePath), "file", "L1");

        // ── Walk all type declarations ────────────────────────────────────────

        // Determine parent container for any syntax node
        string ContainerNid(SyntaxNode node)
        {
            var parent = node.Parent;
            while (parent != null)
            {
                switch (parent)
                {
                    case ClassBlockSyntax cb:
                        return Helpers.MakeId(stem, cb.ClassStatement.Identifier.Text);
                    case ModuleBlockSyntax mb:
                        return Helpers.MakeId(stem, mb.ModuleStatement.Identifier.Text);
                    case InterfaceBlockSyntax ib:
                        return Helpers.MakeId(stem, ib.InterfaceStatement.Identifier.Text);
                    case StructureBlockSyntax sb:
                        return Helpers.MakeId(stem, sb.StructureStatement.Identifier.Text);
                    case EnumBlockSyntax eb:
                        return Helpers.MakeId(stem, eb.EnumStatement.Identifier.Text);
                    case NamespaceBlockSyntax nb:
                        return Helpers.MakeId(stem, nb.NamespaceStatement.Name.ToString());
                }
                parent = parent.Parent;
            }
            return fileNid;
        }

        // Imports
        foreach (var imp in root.DescendantNodes().OfType<ImportsStatementSyntax>())
        {
            foreach (var clause in imp.ImportsClauses)
            {
                string ns;
                if (clause is SimpleImportsClauseSyntax simple)
                    ns = simple.Name.ToString();
                else
                    continue;

                var parts = ns.Split('.');
                var leaf  = parts.Last();
                var tgtId = Helpers.MakeId(leaf);
                AddNode(tgtId, leaf, "import", Helpers.LineRef(imp));
                AddEdge(fileNid, tgtId, "imports", Helpers.LineRef(imp));
            }
        }

        // Namespaces
        foreach (var ns in root.DescendantNodes().OfType<NamespaceBlockSyntax>())
        {
            var name  = ns.NamespaceStatement.Name.ToString();
            var nid   = Helpers.MakeId(stem, name);
            var lr    = Helpers.LineRef(ns.NamespaceStatement);
            AddNode(nid, name, "namespace", lr);
            AddEdge(fileNid, nid, "contains", lr);
        }

        // Classes
        foreach (var cb in root.DescendantNodes().OfType<ClassBlockSyntax>())
        {
            var name = cb.ClassStatement.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var nid = Helpers.MakeId(stem, name);
            var lr  = Helpers.LineRef(cb.ClassStatement);
            AddNode(nid, name, "class", lr);
            AddEdge(ContainerNid(cb), nid, "contains", lr);

            // Semantics: resolve base class
            var sym = model.GetDeclaredSymbol(cb) as INamedTypeSymbol;
            if (sym != null)
            {
                // Base class
                if (sym.BaseType != null && sym.BaseType.SpecialType == SpecialType.None)
                {
                    var baseName = sym.BaseType.Name;
                    var baseId   = Helpers.MakeId(stem, baseName);
                    if (!seen.Contains(baseId)) baseId = Helpers.MakeId(baseName);
                    if (!seen.Contains(baseId)) AddNode(baseId, baseName, "class", lr);
                    AddEdge(nid, baseId, "extends", lr, "SEMANTIC");
                }
                // Interfaces
                foreach (var iface in sym.Interfaces)
                {
                    var ifName = iface.Name;
                    var ifId   = Helpers.MakeId(stem, ifName);
                    if (!seen.Contains(ifId)) ifId = Helpers.MakeId(ifName);
                    if (!seen.Contains(ifId)) AddNode(ifId, ifName, "interface", lr);
                    AddEdge(nid, ifId, "implements", lr, "SEMANTIC");
                }
            }
        }

        // Modules
        foreach (var mb in root.DescendantNodes().OfType<ModuleBlockSyntax>())
        {
            var name = mb.ModuleStatement.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var nid = Helpers.MakeId(stem, name);
            var lr  = Helpers.LineRef(mb.ModuleStatement);
            AddNode(nid, name, "module", lr);
            AddEdge(ContainerNid(mb), nid, "contains", lr);
        }

        // Interfaces
        foreach (var ib in root.DescendantNodes().OfType<InterfaceBlockSyntax>())
        {
            var name = ib.InterfaceStatement.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var nid = Helpers.MakeId(stem, name);
            var lr  = Helpers.LineRef(ib.InterfaceStatement);
            AddNode(nid, name, "interface", lr);
            AddEdge(ContainerNid(ib), nid, "contains", lr);

            // Semantic: base interfaces
            var sym = model.GetDeclaredSymbol(ib) as INamedTypeSymbol;
            if (sym != null)
            {
                foreach (var iface in sym.Interfaces)
                {
                    var ifName = iface.Name;
                    var ifId   = Helpers.MakeId(stem, ifName);
                    if (!seen.Contains(ifId)) ifId = Helpers.MakeId(ifName);
                    if (!seen.Contains(ifId)) AddNode(ifId, ifName, "interface", lr);
                    AddEdge(nid, ifId, "extends", lr, "SEMANTIC");
                }
            }
        }

        // Enums
        foreach (var eb in root.DescendantNodes().OfType<EnumBlockSyntax>())
        {
            var name = eb.EnumStatement.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var nid = Helpers.MakeId(stem, name);
            var lr  = Helpers.LineRef(eb.EnumStatement);
            AddNode(nid, name, "enum", lr);
            AddEdge(ContainerNid(eb), nid, "contains", lr);

            // Enum members
            foreach (var member in eb.Members.OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.EnumMemberDeclarationSyntax>())
            {
                var mName = member.Identifier.Text;
                var mNid  = Helpers.MakeId(stem, name, mName);
                var mLr   = Helpers.LineRef(member);
                AddNode(mNid, mName, "enum_member", mLr);
                AddEdge(nid, mNid, "contains", mLr);
            }
        }

        // Structures
        foreach (var sb in root.DescendantNodes().OfType<StructureBlockSyntax>())
        {
            var name = sb.StructureStatement.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var nid = Helpers.MakeId(stem, name);
            var lr  = Helpers.LineRef(sb.StructureStatement);
            AddNode(nid, name, "struct", lr);
            AddEdge(ContainerNid(sb), nid, "contains", lr);

            // Semantic: interfaces implemented by struct
            var sym = model.GetDeclaredSymbol(sb) as INamedTypeSymbol;
            if (sym != null)
            {
                foreach (var iface in sym.Interfaces)
                {
                    var ifName = iface.Name;
                    var ifId   = Helpers.MakeId(stem, ifName);
                    if (!seen.Contains(ifId)) ifId = Helpers.MakeId(ifName);
                    if (!seen.Contains(ifId)) AddNode(ifId, ifName, "interface", lr);
                    AddEdge(nid, ifId, "implements", lr, "SEMANTIC");
                }
            }
        }

        // Methods: Sub / Function
        foreach (var method in root.DescendantNodes().OfType<MethodBlockSyntax>())
        {
            var stmt = method.SubOrFunctionStatement;
            var name = stmt.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;

            var lr    = Helpers.LineRef(stmt);
            var nid   = Helpers.MakeId(stem, ContainerNid(method).Split('.').Last(), name);
            var label = $"{name}()";
            var kind  = stmt.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.SubStatement) ? "sub" : "function";
            AddNode(nid, label, kind, lr);
            AddEdge(ContainerNid(method), nid, "contains", lr);

            // Semantic: return type
            var sym = model.GetDeclaredSymbol(method) as IMethodSymbol;
            if (sym != null && sym.ReturnType.SpecialType == SpecialType.None
                             && sym.ReturnType.TypeKind != TypeKind.Error)
            {
                var retName = sym.ReturnType.Name;
                if (!string.IsNullOrEmpty(retName))
                {
                    var retId = Helpers.MakeId(stem, retName);
                    if (!seen.Contains(retId)) retId = Helpers.MakeId(retName);
                    if (!seen.Contains(retId)) AddNode(retId, retName, "class", lr);
                    AddEdge(nid, retId, "returns", lr, "SEMANTIC");
                }
            }

            // Semantic: parameter types
            if (sym != null)
            {
                foreach (var p in sym.Parameters)
                {
                    if (p.Type.SpecialType != SpecialType.None) continue;
                    if (p.Type.TypeKind == TypeKind.Error) continue;
                    var pName = p.Type.Name;
                    if (string.IsNullOrEmpty(pName)) continue;
                    var pId = Helpers.MakeId(stem, pName);
                    if (!seen.Contains(pId)) pId = Helpers.MakeId(pName);
                    if (!seen.Contains(pId)) AddNode(pId, pName, "class", lr);
                    AddEdge(nid, pId, "uses_type", lr, "SEMANTIC");
                }
            }
        }

        // Properties
        foreach (var prop in root.DescendantNodes().OfType<PropertyBlockSyntax>())
        {
            var name = prop.PropertyStatement.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var lr  = Helpers.LineRef(prop.PropertyStatement);
            var nid = Helpers.MakeId(stem, ContainerNid(prop).Split('.').Last(), name);
            AddNode(nid, name, "property", lr);
            AddEdge(ContainerNid(prop), nid, "contains", lr);

            // Semantic: property type
            var sym = model.GetDeclaredSymbol(prop.PropertyStatement) as IPropertySymbol;
            if (sym != null && sym.Type.SpecialType == SpecialType.None
                             && sym.Type.TypeKind != TypeKind.Error)
            {
                var tName = sym.Type.Name;
                if (!string.IsNullOrEmpty(tName))
                {
                    var tId = Helpers.MakeId(stem, tName);
                    if (!seen.Contains(tId)) tId = Helpers.MakeId(tName);
                    if (!seen.Contains(tId)) AddNode(tId, tName, "class", lr);
                    AddEdge(nid, tId, "type_of", lr, "SEMANTIC");
                }
            }
        }

        // Auto-properties (single-line: Public Property X As Y)
        foreach (var prop in root.DescendantNodes().OfType<PropertyStatementSyntax>())
        {
            // Skip if already inside a PropertyBlock (handled above)
            if (prop.Parent is PropertyBlockSyntax) continue;
            var name = prop.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var lr  = Helpers.LineRef(prop);
            var nid = Helpers.MakeId(stem, ContainerNid(prop).Split('.').Last(), name);
            AddNode(nid, name, "property", lr);
            AddEdge(ContainerNid(prop), nid, "contains", lr);

            var sym = model.GetDeclaredSymbol(prop) as IPropertySymbol;
            if (sym != null && sym.Type.SpecialType == SpecialType.None
                             && sym.Type.TypeKind != TypeKind.Error)
            {
                var tName = sym.Type.Name;
                if (!string.IsNullOrEmpty(tName))
                {
                    var tId = Helpers.MakeId(stem, tName);
                    if (!seen.Contains(tId)) tId = Helpers.MakeId(tName);
                    if (!seen.Contains(tId)) AddNode(tId, tName, "class", lr);
                    AddEdge(nid, tId, "type_of", lr, "SEMANTIC");
                }
            }
        }

        // Events
        foreach (var ev in root.DescendantNodes().OfType<EventStatementSyntax>())
        {
            var name = ev.Identifier.Text;
            if (Helpers.IsVBKeyword(name)) continue;
            var lr  = Helpers.LineRef(ev);
            var nid = Helpers.MakeId(stem, ContainerNid(ev).Split('.').Last(), name);
            AddNode(nid, name, "event", lr);
            AddEdge(ContainerNid(ev), nid, "contains", lr);
        }

        // Fields (Dim / Private _field As Type)
        foreach (var field in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.FieldDeclarationSyntax>())
        {
            foreach (var declarator in field.Declarators)
            {
                foreach (var nameId in declarator.Names)
                {
                    var name = nameId.Identifier.Text;
                    if (Helpers.IsVBKeyword(name)) continue;
                    var lr  = Helpers.LineRef(field);
                    var nid = Helpers.MakeId(stem, ContainerNid(field).Split('.').Last(), name);
                    AddNode(nid, name, "field", lr);
                    AddEdge(ContainerNid(field), nid, "contains", lr);

                    // Semantic: field type
                    var sym = model.GetDeclaredSymbol(nameId) as IFieldSymbol;
                    if (sym != null && sym.Type.SpecialType == SpecialType.None
                                     && sym.Type.TypeKind != TypeKind.Error)
                    {
                        var tName = sym.Type.Name;
                        if (!string.IsNullOrEmpty(tName))
                        {
                            var tId = Helpers.MakeId(stem, tName);
                            if (!seen.Contains(tId)) tId = Helpers.MakeId(tName);
                            if (!seen.Contains(tId)) AddNode(tId, tName, "class", lr);
                            AddEdge(nid, tId, "type_of", lr, "SEMANTIC");
                        }
                    }
                }
            }
        }

        return new GraphResult(nodes, edges);
    }
}

// ── C# extractor ─────────────────────────────────────────────────────────────

static class CSharpExtractor
{
    public static GraphResult Extract(string filePath)
    {
        var source = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, System.Text.Encoding.UTF8),
            path: filePath);

        var mscorlib = MetadataReference.CreateFromFile(Helpers.ResolveCorLib());
        var compilation = CSharpCompilation.Create(
            "RoslynExtract",
            syntaxTrees: new[] { syntaxTree },
            references: new[] { mscorlib },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(syntaxTree);
        var root  = syntaxTree.GetRoot();

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var seen  = new HashSet<string>();

        var strPath = filePath;
        var stem    = Path.GetFileNameWithoutExtension(filePath);
        var fileNid = Helpers.MakeId(strPath);

        void AddNode(string nid, string label, string nodeType, string lineRef)
        {
            if (seen.Add(nid))
                nodes.Add(new GraphNode(nid, label, "code", strPath, lineRef, nodeType));
        }

        void AddEdge(string src, string tgt, string relation, string lineRef,
                     string confidence = "EXTRACTED")
        {
            edges.Add(new GraphEdge(src, tgt, relation, confidence, strPath, lineRef, 1.0));
        }

        AddNode(fileNid, Path.GetFileName(filePath), "file", "L1");

        string ContainerNid(SyntaxNode node)
        {
            var parent = node.Parent;
            while (parent != null)
            {
                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax cd)
                    return Helpers.MakeId(stem, cd.Identifier.Text);
                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax id2)
                    return Helpers.MakeId(stem, id2.Identifier.Text);
                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax sd)
                    return Helpers.MakeId(stem, sd.Identifier.Text);
                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.EnumDeclarationSyntax ed)
                    return Helpers.MakeId(stem, ed.Identifier.Text);
                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax nd)
                    return Helpers.MakeId(stem, nd.Name.ToString());
                if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax fnd)
                    return Helpers.MakeId(stem, fnd.Name.ToString());
                parent = parent.Parent;
            }
            return fileNid;
        }

        // Usings
        foreach (var u in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>())
        {
            if (u.Name == null) continue;
            var ns    = u.Name.ToString();
            var leaf  = ns.Split('.').Last();
            var tgtId = Helpers.MakeId(leaf);
            AddNode(tgtId, leaf, "import", Helpers.LineRef(u));
            AddEdge(fileNid, tgtId, "imports", Helpers.LineRef(u));
        }

        // Namespaces (traditional + file-scoped)
        foreach (var ns in root.DescendantNodes()
            .Where(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax
                     || n is Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax))
        {
            var name = ns is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax nd
                ? nd.Name.ToString()
                : ((Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax)ns).Name.ToString();
            var nid = Helpers.MakeId(stem, name);
            var lr  = Helpers.LineRef(ns);
            AddNode(nid, name, "namespace", lr);
            AddEdge(fileNid, nid, "contains", lr);
        }

        // Classes
        foreach (var cd in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
        {
            var name = cd.Identifier.Text;
            var nid  = Helpers.MakeId(stem, name);
            var lr   = Helpers.LineRef(cd);
            AddNode(nid, name, "class", lr);
            AddEdge(ContainerNid(cd), nid, "contains", lr);

            var sym = model.GetDeclaredSymbol(cd) as INamedTypeSymbol;
            if (sym != null)
            {
                if (sym.BaseType != null && sym.BaseType.SpecialType == SpecialType.None)
                {
                    var bName = sym.BaseType.Name;
                    var bId   = Helpers.MakeId(stem, bName);
                    if (!seen.Contains(bId)) bId = Helpers.MakeId(bName);
                    if (!seen.Contains(bId)) AddNode(bId, bName, "class", lr);
                    AddEdge(nid, bId, "extends", lr, "SEMANTIC");
                }
                foreach (var iface in sym.Interfaces)
                {
                    var ifName = iface.Name;
                    var ifId   = Helpers.MakeId(stem, ifName);
                    if (!seen.Contains(ifId)) ifId = Helpers.MakeId(ifName);
                    if (!seen.Contains(ifId)) AddNode(ifId, ifName, "interface", lr);
                    AddEdge(nid, ifId, "implements", lr, "SEMANTIC");
                }
            }
        }

        // Interfaces
        foreach (var id2 in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>())
        {
            var name = id2.Identifier.Text;
            var nid  = Helpers.MakeId(stem, name);
            var lr   = Helpers.LineRef(id2);
            AddNode(nid, name, "interface", lr);
            AddEdge(ContainerNid(id2), nid, "contains", lr);
        }

        // Structs
        foreach (var sd in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
        {
            var name = sd.Identifier.Text;
            var nid  = Helpers.MakeId(stem, name);
            var lr   = Helpers.LineRef(sd);
            AddNode(nid, name, "struct", lr);
            AddEdge(ContainerNid(sd), nid, "contains", lr);
        }

        // Enums
        foreach (var ed in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.EnumDeclarationSyntax>())
        {
            var name = ed.Identifier.Text;
            var nid  = Helpers.MakeId(stem, name);
            var lr   = Helpers.LineRef(ed);
            AddNode(nid, name, "enum", lr);
            AddEdge(ContainerNid(ed), nid, "contains", lr);

            foreach (var m in ed.Members)
            {
                var mName = m.Identifier.Text;
                var mNid  = Helpers.MakeId(stem, name, mName);
                var mLr   = Helpers.LineRef(m);
                AddNode(mNid, mName, "enum_member", mLr);
                AddEdge(nid, mNid, "contains", mLr);
            }
        }

        // Methods
        foreach (var md2 in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
        {
            var name  = md2.Identifier.Text;
            var lr    = Helpers.LineRef(md2);
            var nid   = Helpers.MakeId(stem, ContainerNid(md2).Split('.').Last(), name);
            AddNode(nid, $"{name}()", "method", lr);
            AddEdge(ContainerNid(md2), nid, "contains", lr);

            var sym = model.GetDeclaredSymbol(md2) as IMethodSymbol;
            if (sym != null && sym.ReturnType.SpecialType == SpecialType.None
                             && sym.ReturnType.TypeKind != TypeKind.Error)
            {
                var rName = sym.ReturnType.Name;
                if (!string.IsNullOrEmpty(rName))
                {
                    var rId = Helpers.MakeId(stem, rName);
                    if (!seen.Contains(rId)) rId = Helpers.MakeId(rName);
                    if (!seen.Contains(rId)) AddNode(rId, rName, "class", lr);
                    AddEdge(nid, rId, "returns", lr, "SEMANTIC");
                }
            }
        }

        // Properties
        foreach (var pd in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>())
        {
            var name = pd.Identifier.Text;
            var lr   = Helpers.LineRef(pd);
            var nid  = Helpers.MakeId(stem, ContainerNid(pd).Split('.').Last(), name);
            AddNode(nid, name, "property", lr);
            AddEdge(ContainerNid(pd), nid, "contains", lr);

            var sym = model.GetDeclaredSymbol(pd) as IPropertySymbol;
            if (sym != null && sym.Type.SpecialType == SpecialType.None
                             && sym.Type.TypeKind != TypeKind.Error)
            {
                var tName = sym.Type.Name;
                if (!string.IsNullOrEmpty(tName))
                {
                    var tId = Helpers.MakeId(stem, tName);
                    if (!seen.Contains(tId)) tId = Helpers.MakeId(tName);
                    if (!seen.Contains(tId)) AddNode(tId, tName, "class", lr);
                    AddEdge(nid, tId, "type_of", lr, "SEMANTIC");
                }
            }
        }

        // Fields
        foreach (var fd in root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
        {
            foreach (var v in fd.Declaration.Variables)
            {
                var name = v.Identifier.Text;
                var lr   = Helpers.LineRef(fd);
                var nid  = Helpers.MakeId(stem, ContainerNid(fd).Split('.').Last(), name);
                AddNode(nid, name, "field", lr);
                AddEdge(ContainerNid(fd), nid, "contains", lr);
            }
        }

        return new GraphResult(nodes, edges);
    }
}
