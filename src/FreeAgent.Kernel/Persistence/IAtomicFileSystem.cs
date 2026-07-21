namespace FreeAgent.Kernel;

public interface IAtomicFileSystem
{
    ValueTask<string> CreateTempPathAsync(string finalPath, CancellationToken cancellationToken);
    ValueTask WriteTempAsync(string path, string content, CancellationToken cancellationToken);
    ValueTask FsyncTempAsync(string path, CancellationToken cancellationToken);
    ValueTask RenameAsync(string tempPath, string finalPath, CancellationToken cancellationToken);
    ValueTask FsyncDirectoryAsync(string finalPath, CancellationToken cancellationToken);

    /// <summary>
    /// Crash-safe write of <paramref name="content"/> to <paramref name="finalPath"/>: write a temp
    /// file → fsync it → atomically rename over the target → fsync the directory. A crash at any point
    /// leaves either the old file or the complete new one — never a truncated/half-written file. Used
    /// by the editing tools so an interrupted write can't corrupt the user's source.
    /// </summary>
    async ValueTask WriteAllTextAtomicAsync(string finalPath, string content, CancellationToken cancellationToken)
    {
        var temp = await CreateTempPathAsync(finalPath, cancellationToken);
        await WriteTempAsync(temp, content, cancellationToken);
        await FsyncTempAsync(temp, cancellationToken);
        await RenameAsync(temp, finalPath, cancellationToken);
        await FsyncDirectoryAsync(finalPath, cancellationToken);
    }
}
