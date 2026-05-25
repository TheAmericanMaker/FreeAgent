namespace FreeAgent.Kernel;

public interface IAtomicFileSystem
{
    ValueTask WriteTempAsync(string path, string content, CancellationToken cancellationToken);
    ValueTask FsyncTempAsync(string path, CancellationToken cancellationToken);
    ValueTask RenameAsync(string tempPath, string finalPath, CancellationToken cancellationToken);
}
