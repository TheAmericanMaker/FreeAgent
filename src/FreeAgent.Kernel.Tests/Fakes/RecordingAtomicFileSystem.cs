using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class RecordingAtomicFileSystem : IAtomicFileSystem
{
    private int _tempCount;

    public List<string> Operations { get; } = [];
    public List<string> TempPaths { get; } = [];

    public ValueTask<string> CreateTempPathAsync(string finalPath, CancellationToken cancellationToken)
    {
        Operations.Add("create-temp");
        var directory = Path.GetDirectoryName(finalPath) ?? ".";
        var fileName = Path.GetFileName(finalPath);
        var tempPath = Path.Combine(directory, $".{fileName}.{++_tempCount}.tmp");
        TempPaths.Add(tempPath);
        return ValueTask.FromResult(tempPath);
    }

    public ValueTask WriteTempAsync(string path, string content, CancellationToken cancellationToken) { Operations.Add("write-temp"); return ValueTask.CompletedTask; }
    public ValueTask FsyncTempAsync(string path, CancellationToken cancellationToken) { Operations.Add("fsync-temp"); return ValueTask.CompletedTask; }
    public ValueTask RenameAsync(string tempPath, string finalPath, CancellationToken cancellationToken) { Operations.Add("rename"); return ValueTask.CompletedTask; }
    public ValueTask FsyncDirectoryAsync(string finalPath, CancellationToken cancellationToken) { Operations.Add("fsync-directory"); return ValueTask.CompletedTask; }
}
