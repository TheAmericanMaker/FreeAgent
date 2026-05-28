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
        "Roslyn-backed syntactic analysis for C# files. Action 'list-types' lists every "
        + "class/interface/struct/record/enum/delegate declaration as 'file:line: kind Namespace.Type'. "
        + "'list-members' lists methods/properties/fields per type. 'diagnostics' surfaces parse errors. "
        + "Takes 'action' (required) and optional 'path' (file or directory, defaults to the workspace root) "
        + "and 'glob' (e.g. 'src/**/*.cs'). Output capped to keep results bounded.";
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonDocument InputSchema { get; } = JsonDocument.Parse(
        """{"type":"object","required":["action"],"properties":{"action":{"type":"string","enum":["list-types","list-members","diagnostics"]},"path":{"type":"string"},"glob":{"type":"string"}}}""");

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
                    return ToolResult.InvalidInput($"Unknown action '{action}'. Use 'list-types', 'list-members', or 'diagnostics'.");
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
