using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class RecordingAtomicFileSystem : IAtomicFileSystem
{
    public List<string> Operations { get; } = [];
    public ValueTask WriteTempAsync(string path, string content, CancellationToken cancellationToken) { Operations.Add("write-temp"); return ValueTask.CompletedTask; }
    public ValueTask FsyncTempAsync(string path, CancellationToken cancellationToken) { Operations.Add("fsync-temp"); return ValueTask.CompletedTask; }
    public ValueTask RenameAsync(string tempPath, string finalPath, CancellationToken cancellationToken) { Operations.Add("rename"); return ValueTask.CompletedTask; }
}
