/// <summary>
/// Roslyn-based extractor for VB.NET and C# files.
/// Outputs JSON { nodes: [...], edges: [...] } to stdout.
///
/// Modes:
///   roslyn-extractor <file.vb|file.cs>
///       Single-file mode — one JSON result on stdout.
///
///   roslyn-extractor --batch <file1> <file2> ...
///       Batch mode — one JSON result per file, separated by newlines (NDJSON).
///       JIT cost paid once; ~10-20× faster than N separate subprocess calls.
///
///   roslyn-extractor --project <dir>
///       Project mode — all .vb (or .cs) files in dir compiled together into ONE
///       Compilation so Roslyn resolves cross-file type references semantically.
///       Output: merged {nodes, edges} for the whole project.
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

Console.OutputEncoding = System.Text.Encoding.UTF8;

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = false,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

// ── Entry point (top-level statements MUST precede type declarations) ────────

if (args.Length < 1)
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  roslyn-extractor <file.vb|file.cs>\n" +
        "  roslyn-extractor --batch <file1> <file2> ...\n" +
        "  roslyn-extractor --project <directory>");
    Environment.Exit(1);
}

if (args[0] == "--batch")
{
    // ── BATCH MODE: one JSON line per file (NDJSON) ──────────────────────────
    var files = args.Skip(1).ToArray();
    if (files.Length == 0)
    {
        Console.Error.WriteLine("--batch requires at least one file path");
        Environment.Exit(1);
    }
    foreach (var fp in files)
    {
        GraphResult r;
        if (!File.Exists(fp))
        {
            r = new GraphResult(new List<GraphNode>(), new List<GraphEdge>(), new List<RawCall>());
            Console.Error.WriteLine($"[skip] not found: {fp}");
        }
        else
        {
            var e2 = Path.GetExtension(fp).ToLowerInvariant();
            r = e2 == ".vb"  ? VBExtractor.Extract(fp)
              : e2 == ".cs"  ? CSharpExtractor.Extract(fp)
              : new GraphResult(new List<GraphNode>(), new List<GraphEdge>(), new List<RawCall>());
        }
        Console.WriteLine(JsonSerializer.Serialize(r, jsonOpts));
    }
}
else if (args[0] == "--project")
{
    // ── PROJECT MODE: multi-file semantic compilation ────────────────────────
    if (args.Length < 2)
    {
        Console.Error.WriteLine("--project requires a directory path");
        Environment.Exit(1);
    }
    var dir = args[1];
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"Directory not found: {dir}");
        Environment.Exit(1);
    }

    var vbFiles = Directory.GetFiles(dir, "*.vb", SearchOption.AllDirectories);
    var csFiles = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);

    GraphResult result;
    if (vbFiles.Length > 0 && csFiles.Length == 0)
        result = VBExtractor.ExtractProject(vbFiles);
    else if (csFiles.Length > 0 && vbFiles.Length == 0)
        result = CSharpExtractor.ExtractProject(csFiles);
    else if (vbFiles.Length > 0)
        result = VBExtractor.ExtractProject(vbFiles);   // mixed: prefer VB
    else
    {
        Console.Error.WriteLine("No .vb or .cs files found in directory");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine(JsonSerializer.Serialize(result, jsonOpts));
}
else if (args[0] == "--project-filelist")
{
    // ── PROJECT MODE from an explicit file list ──────────────────────────────
    // One shared compilation (cross-file semantic call resolution), but the
    // caller picks the files (e.g. to exclude generated/UI noise). Bypasses the
    // OS command-line length limit that --batch hits on large repos.
    if (args.Length < 2)
    {
        Console.Error.WriteLine("--project-filelist requires a list-file path (one path per line)");
        Environment.Exit(1);
    }
    var listFile = args[1];
    if (!File.Exists(listFile))
    {
        Console.Error.WriteLine($"List file not found: {listFile}");
        Environment.Exit(1);
    }
    var all = File.ReadAllLines(listFile)
                  .Select(l => l.Trim())
                  .Where(l => l.Length > 0 && File.Exists(l))
                  .ToArray();
    var vb = all.Where(f => f.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)).ToArray();
    var cs = all.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (vb.Length == 0 && cs.Length == 0)
    {
        Console.Error.WriteLine("List file contains no existing .vb or .cs files");
        Environment.Exit(1);
    }
    // Optional 2nd list: CONTEXT files (compiled for binding, not extracted).
    string[]? ctxVb = null;
    if (args.Length >= 3 && File.Exists(args[2]))
    {
        ctxVb = File.ReadAllLines(args[2])
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) && File.Exists(l))
            .ToArray();
    }
    var result = (cs.Length > 0 && vb.Length == 0)
        ? CSharpExtractor.ExtractProject(cs)
        : VBExtractor.ExtractProject(vb, ctxVb);
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOpts));
}
else
{
    // ── SINGLE FILE MODE ─────────────────────────────────────────────────────
    var filePath = args[0];
    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"File not found: {filePath}");
        Environment.Exit(1);
    }

    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    GraphResult result;

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

    Console.WriteLine(JsonSerializer.Serialize(result, jsonOpts));
}

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

// Unresolved call: callee captured by name; graphify's resolve_cross_file_raw_calls
// links it to a node by label after all files are known (conservative, INFERRED).
record RawCall(
    [property: JsonPropertyName("caller_nid")]      string CallerNid,
    [property: JsonPropertyName("callee")]          string Callee,
    [property: JsonPropertyName("is_member_call")]  bool   IsMemberCall,
    [property: JsonPropertyName("source_file")]     string SourceFile,
    [property: JsonPropertyName("source_location")] string SourceLocation
);

record GraphResult(
    [property: JsonPropertyName("nodes")]     List<GraphNode> Nodes,
    [property: JsonPropertyName("edges")]     List<GraphEdge> Edges,
    [property: JsonPropertyName("raw_calls")] List<RawCall>   RawCalls
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

    static readonly System.Text.RegularExpressions.Regex XmlTag =
        new System.Text.RegularExpressions.Regex(
            "<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    static void AppendCommentLines(string raw, List<string> block)
    {
        foreach (var ln in raw.Split('\n'))
        {
            // strip comment markers: VB ', C# // /* */ ///, XML-doc '''
            var s = ln.Trim().TrimStart('\'', '/', '*').Trim();
            if (s.EndsWith("*/")) s = s.Substring(0, s.Length - 2).Trim();
            s = XmlTag.Replace(s, " ").Trim();
            if (s.Length > 0) block.Add(s);
        }
    }

    /// <summary>
    /// Collect the contiguous block of leading comments (' line-comments and
    /// '''-XML-doc) that immediately precede a VB declaration, as plain text.
    /// A run of 2+ blank lines breaks the association — such a comment belongs
    /// to something else, not this declaration. Returns "" when none.
    /// </summary>
    public static string VBLeadingComment(SyntaxNode node)
    {
        var block = new List<string>();
        int blankRun = 0;
        foreach (var tr in node.GetLeadingTrivia())
        {
            var k = Microsoft.CodeAnalysis.VisualBasic.VisualBasicExtensions.Kind(tr);
            if (k == Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.EndOfLineTrivia)
            {
                blankRun++;
                if (blankRun >= 3) block.Clear();   // 2+ blank lines -> not ours
                continue;
            }
            if (k == Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.WhitespaceTrivia)
                continue;
            if (k != Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.CommentTrivia
                && k != Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.DocumentationCommentTrivia)
                continue;
            blankRun = 0;
            AppendCommentLines(tr.ToFullString(), block);
        }
        return string.Join(" ", block).Trim();
    }

    /// <summary>C# counterpart of <see cref="VBLeadingComment"/> (// , /* */, ///).</summary>
    public static string CSLeadingComment(SyntaxNode node)
    {
        var block = new List<string>();
        int blankRun = 0;
        foreach (var tr in node.GetLeadingTrivia())
        {
            var k = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(tr);
            if (k == Microsoft.CodeAnalysis.CSharp.SyntaxKind.EndOfLineTrivia)
            {
                blankRun++;
                if (blankRun >= 3) block.Clear();
                continue;
            }
            if (k == Microsoft.CodeAnalysis.CSharp.SyntaxKind.WhitespaceTrivia)
                continue;
            if (k != Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia
                && k != Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineCommentTrivia
                && k != Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia
                && k != Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia)
                continue;
            blankRun = 0;
            AppendCommentLines(tr.ToFullString(), block);
        }
        return string.Join(" ", block).Trim();
    }
}

// ── VB.NET extractor ─────────────────────────────────────────────────────────

static class VBExtractor
{
    /// <summary>
    /// Multi-file project compilation: all files share ONE Compilation so Roslyn
    /// resolves cross-file type references (e.g. base class defined in another file).
    /// Returns merged nodes+edges for the whole project.
    /// </summary>
    public static GraphResult ExtractProject(string[] filePaths) =>
        ExtractProject(filePaths, null);

    /// <summary>
    /// Project compilation with an optional CONTEXT set: context files are parsed
    /// into the same Compilation so binding resolves (e.g. platform base classes),
    /// but only the extract files produce nodes. Calls to context-only methods are
    /// dropped (they are not graph nodes); calls between extract files resolve
    /// cross-file semantically.
    /// </summary>
    public static GraphResult ExtractProject(string[] extractFiles, string[]? contextFiles)
    {
        var mscorlib = MetadataReference.CreateFromFile(Helpers.ResolveCorLib());

        SyntaxTree Parse(string fp) =>
            VisualBasicSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(fp, System.Text.Encoding.UTF8),
                                System.Text.Encoding.UTF8), path: fp);

        var extractTrees = extractFiles.Where(File.Exists).Select(Parse).ToArray();
        var contextTrees = (contextFiles ?? Array.Empty<string>()).Where(File.Exists).Select(Parse).ToArray();

        var compilation = VisualBasicCompilation.Create(
            "RoslynProject",
            syntaxTrees: extractTrees.Concat(contextTrees),
            references: new[] { mscorlib },
            options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Only files in the extract set produce nodes / are valid call targets.
        var extractPaths = new HashSet<string>(extractTrees.Select(t => t.FilePath));

        var allNodes = new List<GraphNode>();
        var allEdges = new List<GraphEdge>();
        var allRawCalls = new List<RawCall>();
        var globalSeen = new HashSet<string>();

        foreach (var tree in extractTrees)
        {
            var partial = ExtractFromTree(tree, compilation.GetSemanticModel(tree), globalSeen, extractPaths);
            allNodes.AddRange(partial.Nodes);
            allEdges.AddRange(partial.Edges);
            allRawCalls.AddRange(partial.RawCalls);
        }

        return new GraphResult(allNodes, allEdges, allRawCalls);
    }

    public static GraphResult Extract(string filePath)
    {
        var source = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
        var syntaxTree = VisualBasicSyntaxTree.ParseText(
            SourceText.From(source, System.Text.Encoding.UTF8), path: filePath);

        var mscorlib = MetadataReference.CreateFromFile(Helpers.ResolveCorLib());
        var compilation = VisualBasicCompilation.Create(
            "RoslynExtract",
            syntaxTrees: new[] { syntaxTree },
            references: new[] { mscorlib },
            options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var seen = new HashSet<string>();
        return ExtractFromTree(syntaxTree, compilation.GetSemanticModel(syntaxTree), seen);
    }

    /// <summary>Core extraction logic — shared between single-file and project modes.</summary>
    /// <summary>Core extraction logic — shared between single-file and project modes.</summary>
    public static GraphResult ExtractFromTree(
        SyntaxTree syntaxTree, SemanticModel model, HashSet<string> seen,
        HashSet<string>? extractPaths = null)
    {
        var filePath = syntaxTree.FilePath;
        var root  = syntaxTree.GetRoot();

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

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

        // Harvest the leading ' / '''-comment that documents a declaration as a
        // "rationale" node linked to the code it explains (the README "why" layer).
        // This is the developers' own business-language description — extracted
        // locally, no LLM, no guessing.
        void AddRationale(SyntaxNode decl, string parentNid)
        {
            var text = Helpers.VBLeadingComment(decl);
            if (text.Length == 0) return;
            var line  = decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var rid   = Helpers.MakeId(stem, "rationale", line.ToString());
            var label = text.Length > 80 ? text.Substring(0, 80) : text;
            if (seen.Add(rid))
                nodes.Add(new GraphNode(rid, label, "rationale", strPath, $"L{line}", "rationale"));
            AddEdge(rid, parentNid, "rationale_for", $"L{line}");
        }

        // Call-graph state: method name -> nid (intra-file resolution) + the methods
        // to scan for invocations + unresolved (cross-file) calls.
        var rawCalls = new List<RawCall>();
        var methodNidByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var methodList = new List<(MethodBlockSyntax Block, string Nid)>();

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
            AddRationale(cb, nid);

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
                    AddEdge(nid, baseId, "extends", lr, "EXTRACTED");
                }
                // Interfaces
                foreach (var iface in sym.Interfaces)
                {
                    var ifName = iface.Name;
                    var ifId   = Helpers.MakeId(stem, ifName);
                    if (!seen.Contains(ifId)) ifId = Helpers.MakeId(ifName);
                    if (!seen.Contains(ifId)) AddNode(ifId, ifName, "interface", lr);
                    AddEdge(nid, ifId, "implements", lr, "EXTRACTED");
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
            AddRationale(mb, nid);
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
            AddRationale(ib, nid);

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
                    AddEdge(nid, ifId, "extends", lr, "EXTRACTED");
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
            AddRationale(eb, nid);

            // Enum members
            foreach (var member in eb.Members.OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.EnumMemberDeclarationSyntax>())
            {
                var mName = member.Identifier.Text;
                var mNid  = Helpers.MakeId(stem, name, mName);
                var mLr   = Helpers.LineRef(member);
                AddNode(mNid, mName, "enum_member", mLr);
                AddEdge(nid, mNid, "contains", mLr);
                AddRationale(member, mNid);
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
            AddRationale(sb, nid);

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
                    AddEdge(nid, ifId, "implements", lr, "EXTRACTED");
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
            AddRationale(method, nid);
            methodNidByName[name] = nid;
            methodList.Add((method, nid));

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
                    AddEdge(nid, retId, "returns", lr, "EXTRACTED");
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
                    AddEdge(nid, pId, "uses_type", lr, "EXTRACTED");
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
            AddRationale(prop, nid);

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
                    AddEdge(nid, tId, "type_of", lr, "EXTRACTED");
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
            AddRationale(prop, nid);

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
                    AddEdge(nid, tId, "type_of", lr, "EXTRACTED");
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
            AddRationale(ev, nid);
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
                    AddRationale(field, nid);

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
                            AddEdge(nid, tId, "type_of", lr, "EXTRACTED");
                        }
                    }
                }
            }
        }

        // ── Calls: method invocations. Intra-file → "calls" edge now; cross-file →
        // raw_calls, resolved by graphify (resolve_cross_file_raw_calls) by label.
        var callPairs = new HashSet<string>();
        foreach (var (mblock, callerNid) in methodList)
        {
            foreach (var inv in mblock.DescendantNodes()
                         .OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.InvocationExpressionSyntax>())
            {
                // 1) Semantic resolution to a source-declared method (cross-file in --project mode).
                var clrSem = Helpers.LineRef(inv);
                var sym = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (sym != null)
                {
                    var def = sym.OriginalDefinition ?? sym;
                    var declRefs = def.DeclaringSyntaxReferences;
                    if (declRefs.Length > 0 && def.ContainingType != null
                        && !string.IsNullOrEmpty(def.ContainingType.Name))
                    {
                        var tgtFile = declRefs[0].SyntaxTree.FilePath;
                        // Context-only files (platform) bind but are not graph nodes.
                        if (extractPaths == null || extractPaths.Contains(tgtFile))
                        {
                            var declStem = Path.GetFileNameWithoutExtension(tgtFile);
                            var tgtSem = Helpers.MakeId(declStem, def.ContainingType.Name, def.Name);
                            if (tgtSem != callerNid && callPairs.Add(callerNid + "\t" + tgtSem))
                                edges.Add(new GraphEdge(callerNid, tgtSem, "calls", "EXTRACTED", strPath, clrSem, 1.0));
                        }
                        continue;
                    }
                }

                // 2)/3) Syntactic fallback (single-file / batch mode: no cross-file compilation).
                string callee = "";
                bool isMember = false;
                if (inv.Expression is Microsoft.CodeAnalysis.VisualBasic.Syntax.IdentifierNameSyntax idn)
                    callee = idn.Identifier.Text;
                else if (inv.Expression is Microsoft.CodeAnalysis.VisualBasic.Syntax.MemberAccessExpressionSyntax mae)
                {
                    callee = mae.Name.Identifier.Text;
                    isMember = true;
                }
                if (callee.Length == 0 || Helpers.IsVBKeyword(callee)) continue;
                var clr = Helpers.LineRef(inv);
                if (methodNidByName.TryGetValue(callee, out var tgt))
                {
                    if (tgt == callerNid) continue;
                    if (callPairs.Add(callerNid + "" + tgt))
                        edges.Add(new GraphEdge(callerNid, tgt, "calls", "EXTRACTED", strPath, clr, 1.0));
                }
                else
                {
                    rawCalls.Add(new RawCall(callerNid, callee, isMember, strPath, clr));
                }
            }
        }

        return new GraphResult(nodes, edges, rawCalls);
    }
}

// ── C# extractor ─────────────────────────────────────────────────────────────

static class CSharpExtractor
{
    public static GraphResult ExtractProject(string[] filePaths)
    {
        var mscorlib = MetadataReference.CreateFromFile(Helpers.ResolveCorLib());
        var trees = filePaths
            .Where(File.Exists)
            .Select(fp => CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(fp, System.Text.Encoding.UTF8),
                                System.Text.Encoding.UTF8), path: fp))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "RoslynProject",
            syntaxTrees: trees,
            references: new[] { mscorlib },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var allNodes  = new List<GraphNode>();
        var allEdges  = new List<GraphEdge>();
        var globalSeen = new HashSet<string>();

        foreach (var tree in trees)
        {
            var partial = Extract(tree.FilePath, compilation.GetSemanticModel(tree), globalSeen);
            allNodes.AddRange(partial.Nodes);
            allEdges.AddRange(partial.Edges);
        }
        return new GraphResult(allNodes, allEdges, new List<RawCall>());
    }


    public static GraphResult Extract(string filePath)
    {
        var source = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, System.Text.Encoding.UTF8), path: filePath);

        var mscorlib = MetadataReference.CreateFromFile(Helpers.ResolveCorLib());
        var compilation = CSharpCompilation.Create(
            "RoslynExtract",
            syntaxTrees: new[] { syntaxTree },
            references: new[] { mscorlib },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Extract(filePath, compilation.GetSemanticModel(syntaxTree), new HashSet<string>());
    }

    public static GraphResult Extract(string filePath, SemanticModel model, HashSet<string> seen)
    {
        var root  = model.SyntaxTree.GetRoot();

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

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
                    AddEdge(nid, bId, "extends", lr, "EXTRACTED");
                }
                foreach (var iface in sym.Interfaces)
                {
                    var ifName = iface.Name;
                    var ifId   = Helpers.MakeId(stem, ifName);
                    if (!seen.Contains(ifId)) ifId = Helpers.MakeId(ifName);
                    if (!seen.Contains(ifId)) AddNode(ifId, ifName, "interface", lr);
                    AddEdge(nid, ifId, "implements", lr, "EXTRACTED");
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
                    AddEdge(nid, rId, "returns", lr, "EXTRACTED");
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
                    AddEdge(nid, tId, "type_of", lr, "EXTRACTED");
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

        return new GraphResult(nodes, edges, new List<RawCall>());
    }
}
