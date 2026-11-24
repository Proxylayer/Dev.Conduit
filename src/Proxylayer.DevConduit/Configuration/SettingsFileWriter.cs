using System.Security.AccessControl;
using System.Text;
using System.Text.Json.Nodes;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Applies a <see cref="SettingsChangeSet"/> to one settings file, leaving every key it does
/// not name as it was. The file is replaced in one step: the new content goes to a temporary
/// file beside it, which is then moved over the original, so a reader sees the old file or
/// the new one and never half of either. Comments in the file are not preserved.
/// </summary>
/// <remarks>
/// <see cref="Services.AtomicJsonFile"/> serializes typed storage objects with the storage
/// naming policy and a fixed temporary name; this writer needs a JSON document, a random
/// temporary name, and an access list set when the temporary file is created.
/// </remarks>
public sealed class SettingsFileWriter
{
    private readonly Func<FileSecurity>? _security;

    /// <param name="path">The settings file.</param>
    /// <param name="security">
    /// The access list the file is written with, or null to inherit from its folder. The
    /// temporary file is created with it, and the move keeps it.
    /// </param>
    public SettingsFileWriter(string path, Func<FileSecurity>? security = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"'{path}' is not a fully qualified path.", nameof(path));
        }

        Path = System.IO.Path.GetFullPath(path);
        _security = security;
    }

    public string Path { get; }

    /// <summary>The current document, or an empty one when the file is absent or blank.</summary>
    /// <exception cref="InvalidDataException">The file is not a JSON object; the message names it, and it is never overwritten then.</exception>
    public JsonObject Read()
    {
        if (!File.Exists(Path))
        {
            return new JsonObject(SettingsJson.NodeOptions);
        }

        try
        {
            return SettingsJson.Parse(File.ReadAllText(Path));
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException($"The settings file {Path} is not valid JSON, so it was left as it is: {exception.Message}", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"The settings file {Path} is not a JSON object, so it was left as it is: {exception.Message}", exception);
        }
    }

    /// <summary>Applies the changes and writes the file. Returns the document written.</summary>
    public JsonObject Apply(SettingsChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var document = Read();
        changes.ApplyTo(document);
        Write(document);
        return document;
    }

    private void Write(JsonObject document)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(document.ToJsonString(SettingsJson.WriteOptions) + Environment.NewLine);
        var temporaryPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = Create(temporaryPath))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private FileStream Create(string temporaryPath)
    {
        if (_security is null || !OperatingSystem.IsWindows())
        {
            return new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        return new FileInfo(temporaryPath).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.Synchronize,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            _security());
    }
}
