namespace FreeAgent.Kernel;

/// <summary>A pre-write snapshot of a file (<see cref="PreviousContent"/> is null if the file didn't exist).</summary>
public sealed record FileSnapshot(string Path, string? PreviousContent, DateTimeOffset At);

/// <summary>
/// In-memory LIFO history of file mutations for the current session — used by the host's
/// <c>/undo</c> command. Tools (<see cref="WriteFileTool"/>, <see cref="EditFileTool"/>) record a
/// snapshot after a successful write; an undo pops the most recent snapshot and restores it
/// (or deletes the file if it didn't exist before). In-memory only; not persisted across runs.
/// </summary>
public sealed class FileHistory
{
    private readonly Stack<FileSnapshot> _stack = new();

    public int Count => _stack.Count;

    public void Record(string path, string? previousContent) =>
        _stack.Push(new FileSnapshot(path, previousContent, DateTimeOffset.UtcNow));

    public bool TryPop(out FileSnapshot snapshot)
    {
        if (_stack.Count == 0)
        {
            snapshot = null!;
            return false;
        }
        snapshot = _stack.Pop();
        return true;
    }
}
