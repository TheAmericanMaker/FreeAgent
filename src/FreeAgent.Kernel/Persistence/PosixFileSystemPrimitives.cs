using System.Runtime.InteropServices;

namespace FreeAgent.Kernel;

internal static class PosixFileSystemPrimitives
{
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open(string pathname, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int Close(int fd);

    public static void FsyncFileDescriptor(SafeHandle handle)
    {
        if (fsync(handle.DangerousGetHandle().ToInt32()) != 0)
        {
            ThrowLastError("fsync failed");
        }
    }

    public static void FsyncDirectory(string directoryPath)
    {
        var fd = Open(directoryPath, O_RDONLY | O_DIRECTORY);
        if (fd < 0)
        {
            ThrowLastError($"open directory for fsync failed: {directoryPath}");
        }

        try
        {
            if (fsync(fd) != 0)
            {
                ThrowLastError($"fsync directory failed: {directoryPath}");
            }
        }
        finally
        {
            if (Close(fd) != 0)
            {
                ThrowLastError($"close directory failed: {directoryPath}");
            }
        }
    }

    private static void ThrowLastError(string message) =>
        throw new IOException($"{message}: {Marshal.GetLastPInvokeError()}");
}
