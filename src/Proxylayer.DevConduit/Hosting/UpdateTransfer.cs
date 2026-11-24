using System.Text.RegularExpressions;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Carries a downloaded update package from the unelevated tray to the elevated helper, as
/// <see cref="ProxyConfigTransfer"/> carries a <c>.conduit</c>: the package is copied to a new file
/// with a random name in the temp folder, the helper is given that path, copies it into the
/// restricted machine update folder, checks the copy and deletes the transfer file. Only files
/// named as this class names them are ever read or deleted, so the path argument cannot point the
/// helper at anything else.
/// </summary>
public static partial class UpdateTransfer
{
    private const string Prefix = "Dev.Conduit-update-";

    /// <summary>Copies <paramref name="sourcePath"/> to a new transfer file and returns its path.</summary>
    public static string WriteTempFile(string sourcePath, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var path = Path.Combine(directory ?? Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}.msi");
        File.Copy(sourcePath, path, overwrite: false);
        return path;
    }

    /// <summary>True when <paramref name="path"/> is fully qualified and names a transfer file.</summary>
    public static bool IsTransferPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && TransferFileName().IsMatch(Path.GetFileName(path));

    /// <summary>Deletes a transfer file. Does nothing for any other path.</summary>
    public static void Delete(string path)
    {
        if (!IsTransferPath(path))
        {
            return;
        }

        try
        {
            var file = new FileInfo(path);
            if (file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                file.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; the temp folder is cleaned eventually.
        }
    }

    [GeneratedRegex(@"^Dev\.Conduit-update-[0-9a-f]{32}\.msi$", RegexOptions.CultureInvariant)]
    private static partial Regex TransferFileName();
}
