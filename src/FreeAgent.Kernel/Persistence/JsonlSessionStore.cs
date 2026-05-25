using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAgent.Kernel;

public sealed class JsonlSessionStore : IPersistenceStore
{
    private readonly IAtomicFileSystem? _fileSystem;
    private readonly string _path;
    private string? _lastJsonl;

    public JsonlSessionStore(IAtomicFileSystem? fileSystem = null, string path = "session.jsonl")
    {
        _fileSystem = fileSystem;
        _path = path;
    }

    public async ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken)
    {
        var jsonl = await SerializeAsync(state, cancellationToken);
        _lastJsonl = jsonl;
        if (_fileSystem is null)
        {
            return;
        }

        var tempPath = _path + ".tmp";
        await _fileSystem.WriteTempAsync(tempPath, jsonl, cancellationToken);
        await _fileSystem.FsyncTempAsync(tempPath, cancellationToken);
        await _fileSystem.RenameAsync(tempPath, _path, cancellationToken);
    }

    public async ValueTask<SessionState> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_lastJsonl is null)
        {
            throw new InvalidOperationException("No session has been saved in this store.");
        }

        var state = await DeserializeAsync(_lastJsonl, cancellationToken);
        if (state.SessionId != sessionId)
        {
            throw new InvalidOperationException($"Loaded session {state.SessionId}, expected {sessionId}.");
        }

        return state;
    }

    public ValueTask<string> SerializeAsync(SessionState state, CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new SessionHeader(state.SessionId, state.StartedAt, state.WorkingDirectory), JsonOptions.Default)
        };
        lines.AddRange(state.Messages.Select(m => JsonSerializer.Serialize(MessageRecord.FromMessage(m), JsonOptions.Default)));
        return ValueTask.FromResult(string.Join('\n', lines) + "\n");
    }

    public ValueTask<SessionState> DeserializeAsync(string jsonl, CancellationToken cancellationToken)
    {
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new JsonException("Session JSONL is empty.");
        }

        var header = JsonSerializer.Deserialize<SessionHeader>(lines[0], JsonOptions.Default)
            ?? throw new JsonException("Session header is missing or invalid.");
        if (string.IsNullOrWhiteSpace(header.SessionId) || string.IsNullOrWhiteSpace(header.WorkingDirectory))
        {
            throw new JsonException("Session header must be line 0 and include session_id and working_directory.");
        }

        var state = new SessionState(header.SessionId, header.WorkingDirectory, header.StartedAt);
        foreach (var line in lines.Skip(1))
        {
            var record = JsonSerializer.Deserialize<MessageRecord>(line, JsonOptions.Default)
                ?? throw new JsonException("Invalid message record.");
            state.Messages.Add(record.ToMessage());
        }

        return ValueTask.FromResult(state);
    }

    private sealed record SessionHeader(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("working_directory")] string WorkingDirectory);

    private sealed record MessageRecord(
        [property: JsonPropertyName("role")] MessageRole Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<ToolCall>? ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId,
        [property: JsonPropertyName("tool_name")] string? ToolName,
        [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp)
    {
        public static MessageRecord FromMessage(Message message) => new(message.Role, message.Content, message.ToolCalls, message.ToolCallId, message.ToolName, message.Timestamp);
        public Message ToMessage() => new(Role, Content, ToolCalls, ToolCallId, ToolName, Timestamp);
    }
}
