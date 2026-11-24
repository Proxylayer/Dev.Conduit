using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Carries a machine settings change from the unelevated tray to the elevated helper. The
/// tray writes the change set to a new file with a random name in its temp folder and passes
/// the path; the helper reads it once, checks it, applies it to the machine settings file and
/// deletes it. Only files named as this class names them are ever read or deleted, so the
/// path argument cannot point the elevated helper at anything else.
/// </summary>
public static partial class MachineSettingsTransfer
{
    /// <summary>A change set is a few kilobytes; anything far larger is not one.</summary>
    public const long MaxBytes = 256 * 1024;

    private const string Prefix = "Dev.Conduit-settings-";

    /// <summary>Writes the change set to a new temp file and returns its path.</summary>
    public static string WriteTempFile(SettingsChangeSet changes, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var path = Path.Combine(directory ?? Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(changes.ToJson());
        return path;
    }

    /// <summary>True when <paramref name="path"/> is fully qualified and names a transfer file.</summary>
    public static bool IsTransferPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && TransferFileName().IsMatch(Path.GetFileName(path));

    /// <summary>
    /// Reads and checks a transfer file: its name, that it is an ordinary file of a sensible
    /// size, that it parses, and that it touches only keys in <paramref name="allowed"/>.
    /// </summary>
    public static bool TryRead(
        string path,
        IReadOnlySet<string> allowed,
        [NotNullWhen(true)] out SettingsChangeSet? changes,
        out string error)
    {
        changes = null;

        if (!IsTransferPath(path))
        {
            error = $"'{path}' is not a settings transfer file.";
            return false;
        }

        string text;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Attributes.HasFlag(FileAttributes.Directory))
            {
                error = $"'{path}' is not an ordinary file.";
                return false;
            }

            if (file.Length > MaxBytes)
            {
                error = $"'{path}' is larger than a settings change can be.";
                return false;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"'{path}' could not be read: {exception.Message}";
            return false;
        }

        if (!SettingsChangeSet.TryParse(text, out var parsed, out error))
        {
            return false;
        }

        var unmanaged = ManagedSettingKeys.Unmanaged(parsed, allowed);
        if (unmanaged.Count > 0)
        {
            error = $"The change touches keys the setup window does not manage: {string.Join(", ", unmanaged)}.";
            return false;
        }

        changes = parsed;
        return true;
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
            // A leftover temp file is harmless and the temp folder is cleaned eventually.
        }
    }

    [GeneratedRegex(@"^Dev\.Conduit-settings-[0-9a-f]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex TransferFileName();
}
