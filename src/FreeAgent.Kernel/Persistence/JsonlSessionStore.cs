using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAgent.Kernel;

public sealed class JsonlSessionStore : IPersistenceStore
{
    private readonly IAtomicFileSystem _fileSystem;
    private readonly string _path;
    private string? _lastJsonl;

    public JsonlSessionStore(IAtomicFileSystem? fileSystem = null, string path = "session.jsonl")
    {
        _fileSystem = fileSystem ?? new LinuxAtomicFileSystem();
        _path = path;
    }

    public async ValueTask SaveAsync(SessionState state, CancellationToken cancellationToken)
    {
        var jsonl = await SerializeAsync(state, cancellationToken);
        _lastJsonl = jsonl;
        var tempPath = await _fileSystem.CreateTempPathAsync(_path, cancellationToken);
        try
        {
            await _fileSystem.WriteTempAsync(tempPath, jsonl, cancellationToken);
            await _fileSystem.FsyncTempAsync(tempPath, cancellationToken);
            await _fileSystem.RenameAsync(tempPath, _path, cancellationToken);
            await _fileSystem.FsyncDirectoryAsync(_path, cancellationToken);
        }
        catch
        {
            if (_fileSystem is LinuxAtomicFileSystem)
            {
                TryDeleteTemp(tempPath);
            }

            throw;
        }
    }

    public async ValueTask<SessionState> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        string jsonl;
        if (File.Exists(_path))
        {
            jsonl = await File.ReadAllTextAsync(_path, cancellationToken);
        }
        else if (_lastJsonl is not null)
        {
            jsonl = _lastJsonl;
        }
        else
        {
            throw new FileNotFoundException($"Session JSONL file not found: {_path}", _path);
        }

        var state = await DeserializeAsync(jsonl, cancellationToken);
        if (state.SessionId != sessionId)
        {
            throw new InvalidOperationException($"Loaded session id '{state.SessionId}' does not match expected session id '{sessionId}'.");
        }

        return state;
    }

    public ValueTask<string> SerializeAsync(SessionState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new SessionHeader(state.SessionId, state.StartedAt, state.WorkingDirectory), JsonOptions.Default)
        };
        lines.AddRange(state.Messages.Select(m => JsonSerializer.Serialize(MessageRecord.FromMessage(m), JsonOptions.Default)));
        return ValueTask.FromResult(string.Join('\n', lines) + "\n");
    }

    public ValueTask<SessionState> DeserializeAsync(string jsonl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new JsonException("Session JSONL is empty.");
        }

        SessionHeader header;
        try
        {
            header = JsonSerializer.Deserialize<SessionHeader>(lines[0], JsonOptions.Default)
                ?? throw new JsonException("Session header is missing or invalid.");
        }
        catch (JsonException ex)
        {
            throw new JsonException("Session JSONL header on line 1 is invalid.", ex);
        }

        if (string.IsNullOrWhiteSpace(header.SessionId) || string.IsNullOrWhiteSpace(header.WorkingDirectory))
        {
            throw new JsonException("Session header on line 1 must include session_id and working_directory.");
        }

        var state = new SessionState(header.SessionId, header.WorkingDirectory, header.StartedAt);
        for (var index = 1; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineNumber = index + 1;
            try
            {
                var record = JsonSerializer.Deserialize<MessageRecord>(lines[index], JsonOptions.Default)
                    ?? throw new JsonException("Invalid message record.");
                state.Messages.Add(record.ToMessage());
            }
            catch (JsonException ex)
            {
                throw new JsonException($"Session JSONL message on line {lineNumber} is malformed.", ex);
            }
        }

        return ValueTask.FromResult(state);
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup only; original exception remains authoritative.
        }
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
