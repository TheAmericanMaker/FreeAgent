using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FreeAgent.Kernel;

/// <summary>
/// Roslyn-backed syntactic analysis for C# source. Read-only and concurrency-safe — no semantic
/// model, no compilation, no metadata references; everything is derived from
/// <see cref="CSharpSyntaxTree"/> alone. That keeps the dependency footprint small (parse-only) and
/// the results fast and deterministic. Three actions:
/// <list type="bullet">
///   <item><c>list-types</c> — for every <c>.cs</c> file under <paramref name="path"/>, emits one line
///         per declared class/interface/struct/record/enum/delegate as
///         <c>file:line: kind Namespace.Type</c>.</item>
///   <item><c>list-members</c> — for every type, emits its methods/properties/fields as
///         <c>file:line: kind Type.Member(signature)</c>.</item>
///   <item><c>diagnostics</c> — surfaces syntax-level diagnostics (parse errors) at
///         Warning or higher.</item>
/// </list>
/// Like <see cref="GrepTool"/>, the required capability is a <see cref="FileReadCap"/> on the
/// resolved root, auto-allowed inside the working directory.
/// </summary>
public sealed class CSharpAnalysisTool : ITool
{
    private const int MaxLines = 500;

    public string Name => "CSharpAnalysis";
    public string Description =>
        "Roslyn-backed analysis for C# files. Syntactic actions: 'list-types' lists every "
        + "class/interface/struct/record/enum/delegate declaration as 'file:line: kind Namespace.Type'. "
        + "'list-members' lists methods/properties/fields per type. 'diagnostics' surfaces parse errors. "
        + "Semantic actions (full compilation): 'find-references' returns 'file:line:col' for every "
        + "binding to the named symbol; 'find-definition' returns the declaration site(s); "
        + "'find-callers' returns every caller of a method/property (transitive blast radius "
        + "via 'depth' arg, default 1); 'semantic-diagnostics' surfaces compiler errors and "
        + "warnings (not just parse errors). Takes 'action' (required), 'symbol' (required for "
        + "find-references / find-definition / find-callers), and optional 'path' (file or directory, "
        + "defaults to the workspace root), 'glob' (e.g. 'src/**/*.cs'), and 'depth' (callers, 1–5). "
        + "Output capped to keep results bounded.";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["action"],"properties":{"action":{"type":"string","enum":["list-types","list-members","diagnostics","find-references","find-definition","find-callers","semantic-diagnostics"]},"symbol":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"depth":{"type":"number"}}}""");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new FileReadCap(ResolveRoot(arguments, context))];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var action = arguments.RootElement.GetProperty("action").GetString() ?? "";
        var root = ResolveRoot(arguments, context);
        var globFilter = arguments.RootElement.TryGetProperty("glob", out var g) && g.GetString() is { Length: > 0 } gp
            ? WorkspaceSearch.CompileGlob(gp)
            : null;

        var files = CollectCSharpFiles(root, globFilter);
        if (files.Count == 0)
            return ToolResult.Empty($"No .cs files found under {root}.");

        // Semantic actions need the full compilation; route to that path and return immediately.
        if (action is "find-references" or "find-definition" or "find-callers" or "semantic-diagnostics")
        {
            return await ExecuteSemanticAsync(action, arguments, root, files, context.Session.WorkingDirectory, cancellationToken);
        }

        var sb = new StringBuilder();
        var lines = 0;
        var truncated = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try { text = await File.ReadAllTextAsync(file, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var tree = CSharpSyntaxTree.ParseText(text, path: file, cancellationToken: cancellationToken);
            var relative = WorkspaceSearch.RelativePath(root, file);

            switch (action)
            {
                case "list-types":
                    AppendTypes(sb, tree, relative, ref lines, ref truncated);
                    break;
                case "list-members":
                    AppendMembers(sb, tree, relative, ref lines, ref truncated);
                    break;
                case "diagnostics":
                    AppendDiagnostics(sb, tree, relative, ref lines, ref truncated);
                    break;
                default:
                    return ToolResult.InvalidInput(
                        $"Unknown action '{action}'. Use 'list-types', 'list-members', 'diagnostics', "
                        + "'find-references', 'find-definition', 'find-callers', or 'semantic-diagnostics'.");
            }

            if (truncated)
                break;
        }

        if (sb.Length == 0)
            return ToolResult.Empty($"No results for action '{action}' under {root}.");

        if (truncated)
            sb.Append($"… (truncated at {MaxLines} lines)\n");

        return ToolResult.Success(sb.ToString().TrimEnd('\n'));
    }

    private static async ValueTask<ToolResult> ExecuteSemanticAsync(
        string action, JsonDocument arguments, string root, IReadOnlyList<string> files, string workingDirectory, CancellationToken cancellationToken)
    {
        await Task.Yield(); // building a Compilation is CPU work — give other turns a chance to make progress.

        var sb = new StringBuilder();
        var lines = 0;
        var truncated = false;

        var compilation = RoslynSemanticHelpers.BuildWorkspaceCompilation(files, workingDirectory, cancellationToken);

        if (action == "semantic-diagnostics")
        {
            foreach (var diag in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning))
            {
                var loc = diag.Location.GetLineSpan();
                if (string.IsNullOrEmpty(loc.Path)) continue; // synthetic diagnostics without a source location
                var relative = WorkspaceSearch.RelativePath(root, loc.Path);
                var line = loc.StartLinePosition.Line + 1;
                var col = loc.StartLinePosition.Character + 1;
                if (!Append(sb, $"{relative}:{line}:{col}: {diag.Severity} {diag.Id}: {diag.GetMessage()}", ref lines, ref truncated))
                    break;
            }
        }
        else
        {
            // find-references / find-definition both need a target symbol name.
            if (!arguments.RootElement.TryGetProperty("symbol", out var symProp)
                || symProp.GetString() is not { Length: > 0 } symbolName)
            {
                return ToolResult.InvalidInput($"'{action}' requires a 'symbol' argument (e.g. \"MyClass.Foo\").");
            }

            // Match against the final identifier of a dotted symbol path so callers can pass either
            // "Foo" or "MyClass.Foo". The full path comparison is a string contains, which is loose
            // but matches the most common ask ("find references to Foo").
            var simpleName = symbolName.Contains('.') ? symbolName[(symbolName.LastIndexOf('.') + 1)..] : symbolName;
            var pathHint = symbolName;

            // find-callers walks the call graph transitively across .cs files. depth=1 (default)
            // is direct callers; depth=N (capped at 5) follows callers-of-callers up to N hops.
            if (action == "find-callers")
            {
                var depth = arguments.RootElement.TryGetProperty("depth", out var dProp) && dProp.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(dProp.GetInt32(), 1, 5)
                    : 1;
                return await FindCallersAsync(compilation, simpleName, pathHint, depth, root, sb, lines, truncated, cancellationToken);
            }

            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var relative = WorkspaceSearch.RelativePath(root, tree.FilePath);

                if (action == "find-references")
                {
                    foreach (var ident in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                    {
                        if (ident.Identifier.Text != simpleName) continue;
                        var info = model.GetSymbolInfo(ident);
                        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                        if (symbol is null) continue;
                        if (!SymbolMatches(symbol, simpleName, pathHint)) continue;

                        var pos = ident.GetLocation().GetLineSpan().StartLinePosition;
                        if (!Append(sb, $"{relative}:{pos.Line + 1}:{pos.Character + 1}: {symbol.Kind} {symbol.ToDisplayString()}", ref lines, ref truncated))
                            return Final(sb, lines, truncated, action, root);
                    }
                }
                else // find-definition
                {
                    foreach (var decl in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
                    {
                        var symbol = model.GetDeclaredSymbol(decl);
                        if (symbol is null) continue;
                        if (symbol.Name != simpleName) continue;
                        if (!SymbolMatches(symbol, simpleName, pathHint)) continue;

                        var pos = decl.GetLocation().GetLineSpan().StartLinePosition;
                        if (!Append(sb, $"{relative}:{pos.Line + 1}:{pos.Character + 1}: {symbol.Kind} {symbol.ToDisplayString()}", ref lines, ref truncated))
                            return Final(sb, lines, truncated, action, root);
                    }
                }
            }
        }

        return Final(sb, lines, truncated, action, root);
    }

    /// <summary>
    /// Walk the call graph from the named symbol outward to <paramref name="maxDepth"/> hops.
    /// Output is grouped by depth so the model can see the immediate callers, then their callers,
    /// etc. — the "blast radius" of changing the target. Uses a single <see cref="Compilation"/>
    /// and per-tree <see cref="SemanticModel"/>s; no Workspace is involved (keeps the dependency
    /// matrix the same as <c>find-references</c>'s).
    /// </summary>
    private static async ValueTask<ToolResult> FindCallersAsync(
        CSharpCompilation compilation,
        string simpleName,
        string pathHint,
        int maxDepth,
        string root,
        StringBuilder sb,
        int lines,
        bool truncated,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        // Collect the seed symbols matching the requested name.
        var seedSymbols = new List<ISymbol>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(decl);
                if (symbol is null) continue;
                if (symbol.Name != simpleName) continue;
                if (!SymbolMatches(symbol, simpleName, pathHint)) continue;
                seedSymbols.Add(symbol);
            }
        }
        if (seedSymbols.Count == 0)
            return ToolResult.Empty($"No symbol matches '{pathHint}' in the workspace.");

        // BFS outward from each seed. A symbol is "visited" by its ToDisplayString to handle
        // overloads being distinct but partial declarations being the same.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in seedSymbols) visited.Add(s.ToDisplayString());

        var frontier = seedSymbols.Select(s => (Symbol: s, Depth: 0)).ToList();

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextFrontier = new List<(ISymbol Symbol, int Depth)>();

            foreach (var (target, depth) in frontier)
            {
                if (depth >= maxDepth) continue;

                foreach (var callerTree in compilation.SyntaxTrees)
                {
                    var callerModel = compilation.GetSemanticModel(callerTree);
                    var relative = WorkspaceSearch.RelativePath(root, callerTree.FilePath);

                    foreach (var invocation in callerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var info = callerModel.GetSymbolInfo(invocation);
                        var invoked = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                        if (invoked is null) continue;
                        if (!SymbolEqualityComparer.Default.Equals(invoked.OriginalDefinition, target.OriginalDefinition))
                            continue;

                        // Find the enclosing member declaration so we can attribute the call.
                        var enclosing = invocation.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                        var enclosingSymbol = enclosing is null ? null : callerModel.GetDeclaredSymbol(enclosing);

                        var pos = invocation.GetLocation().GetLineSpan().StartLinePosition;
                        var enclosingDesc = enclosingSymbol?.ToDisplayString() ?? "<unknown>";
                        if (!Append(sb, $"depth {depth + 1}: {relative}:{pos.Line + 1}:{pos.Character + 1}: {enclosingDesc} calls {target.ToDisplayString()}", ref lines, ref truncated))
                            return Final(sb, lines, truncated, "find-callers", root);

                        if (enclosingSymbol is not null && visited.Add(enclosingSymbol.ToDisplayString()))
                            nextFrontier.Add((enclosingSymbol, depth + 1));
                    }
                }
            }

            frontier = nextFrontier;
        }

        return Final(sb, lines, truncated, "find-callers", root);
    }

    private static ToolResult Final(StringBuilder sb, int lines, bool truncated, string action, string root)
    {
        if (sb.Length == 0)
            return ToolResult.Empty($"No results for action '{action}' under {root}.");
        if (truncated)
            sb.Append($"… (truncated at {MaxLines} lines)\n");
        return ToolResult.Success(sb.ToString().TrimEnd('\n'));
    }

    /// <summary>
    /// Matches a Roslyn symbol against the user's requested target. Final-identifier match is the
    /// minimum; if the user gave a dotted path, also require the symbol's containing type's name to
    /// appear in the requested string (loose enough to allow either "MyClass.Foo" or "Foo", strict
    /// enough to avoid matching every method called "Add").
    /// </summary>
    private static bool SymbolMatches(ISymbol symbol, string simpleName, string pathHint)
    {
        if (symbol.Name != simpleName) return false;
        if (!pathHint.Contains('.')) return true;
        var owner = symbol.ContainingType?.Name;
        return owner is null || pathHint.Contains(owner, StringComparison.Ordinal);
    }

    private static List<string> CollectCSharpFiles(string root, System.Text.RegularExpressions.Regex? globFilter)
    {
        var files = new List<string>();
        if (File.Exists(root))
        {
            if (root.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                files.Add(root);
            return files;
        }
        if (!Directory.Exists(root))
            return files;

        foreach (var file in WorkspaceSearch.EnumerateFiles(root))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            if (globFilter is not null)
            {
                var relative = WorkspaceSearch.RelativePath(root, file);
                if (!globFilter.IsMatch(relative))
                    continue;
            }
            files.Add(file);
        }
        return files;
    }

    private static void AppendTypes(StringBuilder sb, SyntaxTree tree, string relative, ref int lines, ref bool truncated)
    {
        var rootNode = tree.GetRoot();
        foreach (var node in rootNode.DescendantNodes())
        {
            string? kind = null;
            string? name = null;
            switch (node)
            {
                case ClassDeclarationSyntax c: kind = "class"; name = c.Identifier.Text; break;
                case InterfaceDeclarationSyntax i: kind = "interface"; name = i.Identifier.Text; break;
                case StructDeclarationSyntax s: kind = "struct"; name = s.Identifier.Text; break;
                case RecordDeclarationSyntax r: kind = r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record"; name = r.Identifier.Text; break;
                case EnumDeclarationSyntax e: kind = "enum"; name = e.Identifier.Text; break;
                case DelegateDeclarationSyntax d: kind = "delegate"; name = d.Identifier.Text; break;
            }
            if (kind is null || name is null) continue;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var ns = QualifiedName(node);
            var fullName = ns.Length == 0 ? name : $"{ns}.{name}";
            if (!Append(sb, $"{relative}:{line}: {kind} {fullName}", ref lines, ref truncated)) return;
        }
    }

    private static void AppendMembers(StringBuilder sb, SyntaxTree tree, string relative, ref int lines, ref bool truncated)
    {
        var rootNode = tree.GetRoot();
        foreach (var type in rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeName = type.Identifier.Text;
            foreach (var member in type.Members)
            {
                var (kind, signature) = DescribeMember(member);
                if (kind is null || signature is null) continue;

                var line = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (!Append(sb, $"{relative}:{line}: {kind} {typeName}.{signature}", ref lines, ref truncated)) return;
            }
        }
    }

    private static (string? Kind, string? Signature) DescribeMember(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => ("method", $"{m.Identifier.Text}{Parameters(m.ParameterList)}"),
        ConstructorDeclarationSyntax c => ("ctor", $"{c.Identifier.Text}{Parameters(c.ParameterList)}"),
        PropertyDeclarationSyntax p => ("property", p.Identifier.Text),
        FieldDeclarationSyntax f => ("field", string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text))),
        EventDeclarationSyntax e => ("event", e.Identifier.Text),
        IndexerDeclarationSyntax i => ("indexer", $"this{Parameters(i.ParameterList)}"),
        _ => (null, null)
    };

    private static string Parameters(BaseParameterListSyntax parameters)
    {
        if (parameters.Parameters.Count == 0) return "()";
        return "(" + string.Join(", ", parameters.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")";
    }

    private static void AppendDiagnostics(StringBuilder sb, SyntaxTree tree, string relative, ref int lines, ref bool truncated)
    {
        foreach (var diag in tree.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning))
        {
            var span = diag.Location.GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            var col = span.StartLinePosition.Character + 1;
            if (!Append(sb, $"{relative}:{line}:{col}: {diag.Severity} {diag.Id}: {diag.GetMessage()}", ref lines, ref truncated)) return;
        }
    }

    private static string QualifiedName(SyntaxNode node)
    {
        var parts = new List<string>();
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case NamespaceDeclarationSyntax ns: parts.Add(ns.Name.ToString()); break;
                case FileScopedNamespaceDeclarationSyntax fns: parts.Add(fns.Name.ToString()); break;
                case ClassDeclarationSyntax c: parts.Add(c.Identifier.Text); break;
                case RecordDeclarationSyntax r: parts.Add(r.Identifier.Text); break;
                case StructDeclarationSyntax s: parts.Add(s.Identifier.Text); break;
                case InterfaceDeclarationSyntax i: parts.Add(i.Identifier.Text); break;
            }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    private static bool Append(StringBuilder sb, string line, ref int lines, ref bool truncated)
    {
        if (lines >= MaxLines)
        {
            truncated = true;
            return false;
        }
        sb.Append(line).Append('\n');
        lines++;
        return true;
    }

    private static string ResolveRoot(JsonDocument arguments, ToolContext context)
    {
        var path = arguments.RootElement.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } sub
            ? sub
            : ".";
        return WorkspacePath.Resolve(path, context.Session.WorkingDirectory);
    }
}
