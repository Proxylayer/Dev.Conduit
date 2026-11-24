using System.Text.RegularExpressions;
using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Carries a <c>.conduit</c> from the unelevated tray or command line to the elevated
/// helper, as <see cref="MachineSettingsTransfer"/> carries a settings change: the file is
/// copied to a new file with a random name in the temp folder, the helper is given that path,
/// reads it once, checks it and deletes it. Only files named as this class names them are
/// ever read or deleted, so the path argument cannot point the helper at anything else. A
/// transfer file is written with the <c>.conduit</c> extension; one named with the legacy
/// <c>.proxyconfig</c> extension is read too.
/// </summary>
public static partial class ProxyConfigTransfer
{
    private const string Prefix = "Dev.Conduit-import-";

    /// <summary>Copies <paramref name="sourcePath"/> to a new transfer file and returns its path.</summary>
    public static string WriteTempFile(string sourcePath, string? directory = null)
    {
        var content = ReadLimited(sourcePath);
        var path = Path.Combine(directory ?? Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}{ProxyConfigDocument.FileExtension}");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
        return path;
    }

    /// <summary>True when <paramref name="path"/> is fully qualified and names a transfer file.</summary>
    public static bool IsTransferPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && TransferFileName().IsMatch(Path.GetFileName(path));

    /// <summary>Reads a transfer file: its name checked, an ordinary file, no larger than a client configuration file can be.</summary>
    public static bool TryRead(string path, out byte[] content, out string error)
    {
        content = [];
        if (!IsTransferPath(path))
        {
            error = $"'{path}' is not a .conduit transfer file.";
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Attributes.HasFlag(FileAttributes.Directory))
            {
                error = $"'{path}' is not an ordinary file.";
                return false;
            }

            content = ReadLimited(path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"'{path}' could not be read: {exception.Message}";
            return false;
        }
    }

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

    /// <summary>Reads a whole file, refusing one larger than a client configuration file can be.</summary>
    /// <exception cref="InvalidDataException">The file is too large.</exception>
    public static byte[] ReadLimited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > ProxyConfigDocument.MaxBytes)
        {
            throw new InvalidDataException($"'{path}' is larger than a .conduit can be.");
        }

        var content = new byte[stream.Length];
        stream.ReadExactly(content);
        return content;
    }

    [GeneratedRegex(@"^Dev\.Conduit-import-[0-9a-f]{32}\.(conduit|proxyconfig)$", RegexOptions.CultureInvariant)]
    private static partial Regex TransferFileName();
}
