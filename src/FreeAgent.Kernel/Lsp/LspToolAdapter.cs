using System.Text;
using System.Text.Json;

namespace FreeAgent.Kernel;

/// <summary>
/// Per-language-server configuration loaded from <c>.freeagent/config.json</c> under
/// <c>lsp.servers[]</c>. <see cref="LanguageId"/> is the LSP language id ("csharp", "typescript",
/// "python", …); <see cref="FileExtensions"/> are the file extensions the agent should consider
/// "owned" by this server (when the agent calls the tool with a path, the adapter only forwards
/// if the path matches). <see cref="Command"/> + <see cref="Args"/> spawn the server binary.
/// </summary>
public sealed record LspServerSpec(string Name, string LanguageId, IReadOnlyList<string> FileExtensions, string Command, IReadOnlyList<string> Args);

public sealed record LspConfig(IReadOnlyList<LspServerSpec> Servers);

/// <summary>
/// Bridges a running <see cref="LspClient"/> to the kernel's tool registry. Registered as
/// <c>lsp__{server}__{action}</c> for each of the four actions — hover, definition, references,
/// open. Required capability is a <see cref="ProcessExecCap"/> on <c>lsp:{server}</c> so a whole
/// language-server can be allow- or deny-ruled as a unit, the same shape <c>McpToolAdapter</c>
/// uses.
/// </summary>
public sealed class LspToolAdapter : ITool
{
    public enum LspAction { Hover, Definition, References, Open }

    private readonly LspClient _client;
    private readonly LspServerSpec _spec;
    private readonly LspAction _action;

    public LspToolAdapter(LspClient client, LspServerSpec spec, LspAction action)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _action = action;
        Name = $"lsp__{spec.Name}__{action.ToString().ToLowerInvariant()}";
        Description = action switch
        {
            LspAction.Hover => $"Hover info from the {spec.Name} language server at a file position. Args: path (workspace-relative), line (1-based), character (1-based).",
            LspAction.Definition => $"Definition lookup via the {spec.Name} language server. Returns 'uri:line:col' per definition. Args: path, line, character.",
            LspAction.References => $"References via the {spec.Name} language server. Returns 'uri:line:col' per reference. Args: path, line, character, optional include_declaration (default true).",
            LspAction.Open => $"Open a file so the {spec.Name} language server has its text loaded. Required before hover/definition/references for most servers. Args: path.",
            _ => $"{spec.Name} LSP action"
        };
        InputSchema = JsonDocument.Parse(action == LspAction.Open
            ? """{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}"""
            : """{"type":"object","required":["path","line","character"],"properties":{"path":{"type":"string"},"line":{"type":"number"},"character":{"type":"number"},"include_declaration":{"type":"boolean"}}}""");
    }

    public string Name { get; }
    public string Description { get; }
    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => false; // shared LspClient state
    public JsonDocument InputSchema { get; }

    public IReadOnlyList<Capability> RequiredCapabilities(JsonDocument arguments, ToolContext context) =>
        [new ProcessExecCap($"lsp:{_spec.Name}", [])];

    public async ValueTask<ToolResult> ExecuteAsync(JsonDocument arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = arguments.RootElement.GetProperty("path").GetString() ?? "";
        var absolute = WorkspacePath.Resolve(path, context.Session.WorkingDirectory);

        if (_spec.FileExtensions.Count > 0
            && !_spec.FileExtensions.Any(ext => absolute.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.InvalidInput(
                $"Path '{path}' isn't handled by the {_spec.Name} language server "
                + $"(expected one of: {string.Join(", ", _spec.FileExtensions)}).");
        }

        var uri = new Uri(absolute).AbsoluteUri;

        try
        {
            switch (_action)
            {
                case LspAction.Open:
                    {
                        if (!File.Exists(absolute))
                            return ToolResult.Error($"File not found: {absolute}");
                        var text = await File.ReadAllTextAsync(absolute, cancellationToken);
                        await _client.DidOpenAsync(uri, _spec.LanguageId, text, cancellationToken);
                        return ToolResult.Success($"Opened {path} on {_spec.Name}.");
                    }
                case LspAction.Hover:
                    {
                        var (line, character) = ReadPosition(arguments);
                        var hover = await _client.HoverAsync(uri, line, character, cancellationToken);
                        return string.IsNullOrEmpty(hover)
                            ? ToolResult.Empty("No hover information at that position.")
                            : ToolResult.Success(hover);
                    }
                case LspAction.Definition:
                    {
                        var (line, character) = ReadPosition(arguments);
                        var defs = await _client.DefinitionAsync(uri, line, character, cancellationToken);
                        return defs.Count == 0
                            ? ToolResult.Empty("No definition found.")
                            : ToolResult.Success(string.Join('\n', defs));
                    }
                case LspAction.References:
                    {
                        var (line, character) = ReadPosition(arguments);
                        var includeDecl = !arguments.RootElement.TryGetProperty("include_declaration", out var ip)
                            || ip.ValueKind != JsonValueKind.False;
                        var refs = await _client.ReferencesAsync(uri, line, character, includeDecl, cancellationToken);
                        return refs.Count == 0
                            ? ToolResult.Empty("No references found.")
                            : ToolResult.Success(string.Join('\n', refs));
                    }
                default:
                    return ToolResult.InvalidInput($"Unknown LSP action: {_action}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return ToolResult.Crash($"LSP request failed: {ex.Message}", retryHint: "Make sure the LSP server is running and the file has been opened.");
        }
    }

    private static (int Line, int Character) ReadPosition(JsonDocument args)
    {
        // LSP positions are 0-based; tool callers think in 1-based lines and columns.
        var line = args.RootElement.GetProperty("line").GetInt32() - 1;
        var character = args.RootElement.GetProperty("character").GetInt32() - 1;
        return (Math.Max(0, line), Math.Max(0, character));
    }
}
