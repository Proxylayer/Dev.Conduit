using System.Text;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// The Windows hosts file on disk. The bytes are kept as they are: a UTF-8 byte order mark
/// stays when there was one, and the rest is read and written as ISO-8859-1, which maps each
/// byte to one character and back, so ANSI and UTF-8 content alike round-trip untouched while
/// <see cref="HostsFileText"/> edits lines. The only text this app adds is its own ASCII line.
/// Writes replace the file in one step through <see cref="File.Replace(string, string, string?)"/>,
/// which keeps the file's own access list and attributes, retrying while another process
/// (often an antivirus scan) has it open. Needs administrator rights for the real file.
/// </summary>
public sealed class HostsFile
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public HostsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    /// <summary>The hosts file of this Windows installation.</summary>
    public static HostsFile ForThisComputer() =>
        new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts"));

    /// <summary>Sets this app's line for <paramref name="hostname"/> and writes the file when that changed it.</summary>
    /// <exception cref="ArgumentException">The address or host name is not plain ASCII.</exception>
    public HostsFileEdit SetEntry(string address, string hostname)
    {
        var original = File.Exists(Path) ? Read() : HostsFileContent.Empty;
        var edit = HostsFileText.SetEntry(original.Text, address, hostname);
        if (edit.Changed)
        {
            Write(original with { Text = edit.Text });
        }

        return edit;
    }

    /// <summary>Removes this app's line for <paramref name="hostname"/> and writes the file when there was one.</summary>
    public HostsFileEdit RemoveEntry(string hostname)
    {
        if (!File.Exists(Path))
        {
            return new HostsFileEdit(string.Empty, Changed: false, []);
        }

        var original = Read();
        var edit = HostsFileText.RemoveEntry(original.Text, hostname);
        if (edit.Changed)
        {
            Write(original with { Text = edit.Text });
        }

        return edit;
    }

    /// <summary>The address this app's line maps <paramref name="hostname"/> to, or null. Reading needs no administrator rights.</summary>
    public string? ManagedAddress(string hostname)
    {
        try
        {
            return File.Exists(Path) ? HostsFileText.ManagedAddress(Read().Text, hostname) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private HostsFileContent Read()
    {
        var bytes = File.ReadAllBytes(Path);
        var bom = bytes.AsSpan().StartsWith(Utf8Bom);
        return new HostsFileContent(bom, Encoding.Latin1.GetString(bom ? bytes[Utf8Bom.Length..] : bytes));
    }

    private void Write(HostsFileContent content)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temporaryPath = System.IO.Path.Combine(directory, $"hosts.{Guid.NewGuid():N}.tmp");
        byte[] bytes = content.ByteOrderMark
            ? [.. Utf8Bom, .. Encoding.Latin1.GetBytes(content.Text)]
            : Encoding.Latin1.GetBytes(content.Text);

        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            if (File.Exists(Path))
            {
                AtomicFile.Retry(() => File.Replace(temporaryPath, Path, destinationBackupFileName: null));
            }
            else
            {
                AtomicFile.Retry(() => File.Move(temporaryPath, Path));
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>The file's text, one character per byte after any UTF-8 byte order mark.</summary>
    private sealed record HostsFileContent(bool ByteOrderMark, string Text)
    {
        public static HostsFileContent Empty { get; } = new(false, string.Empty);
    }
}
