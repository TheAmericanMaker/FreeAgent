using System.Text;

namespace FreeAgent.Kernel;

public sealed class LinuxAtomicFileSystem : IAtomicFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ValueTask<string> CreateTempPathAsync(string finalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(finalPath);
        var tempPath = Path.Combine(directory, $".{fileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        return ValueTask.FromResult(tempPath);
    }

    public async ValueTask WriteTempAsync(string path, string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 16 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous);
        var bytes = Utf8NoBom.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public ValueTask FsyncTempAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
        PosixFileSystemPrimitives.FsyncFileDescriptor(stream.SafeFileHandle);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(string tempPath, string finalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FsyncDirectoryAsync(string finalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? Directory.GetCurrentDirectory();
        PosixFileSystemPrimitives.FsyncDirectory(directory);
        return ValueTask.CompletedTask;
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
            // Best-effort cleanup only; original I/O error remains authoritative.
        }
    }
}
