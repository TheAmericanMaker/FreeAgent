namespace FreeAgent.Kernel;

public interface IAtomicFileSystem
{
    ValueTask<string> CreateTempPathAsync(string finalPath, CancellationToken cancellationToken);
    ValueTask WriteTempAsync(string path, string content, CancellationToken cancellationToken);
    ValueTask FsyncTempAsync(string path, CancellationToken cancellationToken);
    ValueTask RenameAsync(string tempPath, string finalPath, CancellationToken cancellationToken);
    ValueTask FsyncDirectoryAsync(string finalPath, CancellationToken cancellationToken);
}
