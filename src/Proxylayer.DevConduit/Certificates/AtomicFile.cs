using System.Security.AccessControl;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>Replaces a file in one step, retrying briefly while another process holds it.</summary>
public static class AtomicFile
{
    private const int Attempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <param name="path">The file to replace or create.</param>
    /// <param name="content">The new content.</param>
    /// <param name="security">The access list for a new file, or null to inherit from the folder.</param>
    public static void Write(string path, byte[] content, FileSecurity? security = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = Create(temporaryPath, security))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            Retry(() => File.Move(temporaryPath, path, overwrite: true));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/>, retrying while it fails on a sharing or lock violation.</summary>
    public static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException exception) when (attempt < Attempts && IsSharingViolation(exception))
            {
                Thread.Sleep(RetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < Attempts)
            {
                // A file being scanned or replaced by another process can briefly refuse access.
                Thread.Sleep(RetryDelay);
            }
        }
    }

    /// <summary>True for a sharing or lock violation: another process has the file open.</summary>
    internal static bool IsSharingViolation(IOException exception)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        return exception.HResult is SharingViolation or LockViolation;
    }

    private static FileStream Create(string temporaryPath, FileSecurity? security)
    {
        if (security is null || !OperatingSystem.IsWindows())
        {
            return new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        return new FileInfo(temporaryPath).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.Synchronize,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            security);
    }
}
