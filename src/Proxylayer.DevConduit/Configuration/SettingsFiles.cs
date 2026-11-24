using System.Text.Json;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// The two settings files that sit above the appsettings files. Updating Dev.Conduit means
/// extracting a new zip over its folder, which replaces every appsettings file, so anything
/// set on a machine or by a user lives here instead and survives the update:
/// <list type="bullet">
/// <item>machine settings, <c>%ProgramData%\Proxylayer\Dev.Conduit\settings.json</c>, read by every launch;</item>
/// <item>user settings, <c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit\settings.json</c>, read by console and tray
/// launches and never by the service.</item>
/// </list>
/// Both are optional. They are layered directly above the JSON files the host reads from the
/// content root (see <see cref="InsertionIndex"/>), machine first, so environment variables and
/// the command line still win over both.
/// </summary>
public sealed class SettingsFiles
{
    /// <summary>The vendor and product folder under ProgramData and LocalAppData.</summary>
    public const string FolderName = @"Proxylayer\Dev.Conduit";

    public const string FileName = "settings.json";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SettingsFiles(string machinePath, string userPath)
    {
        MachinePath = RequireFullPath(machinePath, nameof(machinePath));
        UserPath = RequireFullPath(userPath, nameof(userPath));
    }

    /// <summary>The machine settings file, read by every launch, including the service.</summary>
    public string MachinePath { get; }

    /// <summary>The user settings file, read by console and tray launches only.</summary>
    public string UserPath { get; }

    /// <summary>The fixed locations on this computer, for the account this process runs as.</summary>
    public static SettingsFiles ForThisComputer() =>
        new(
            Path.Combine(KnownFolder(Environment.SpecialFolder.CommonApplicationData), FolderName, FileName),
            Path.Combine(KnownFolder(Environment.SpecialFolder.LocalApplicationData), FolderName, FileName));

    /// <summary>The files a launch reads, lowest precedence first.</summary>
    public IReadOnlyList<string> PathsFor(bool runningAsService) =>
        runningAsService ? [MachinePath] : [MachinePath, UserPath];

    /// <summary>
    /// Layers the files a launch reads into <paramref name="configuration"/> directly above the
    /// JSON files the host reads from the content root, at <see cref="InsertionIndex"/>. A file
    /// that is absent or empty adds nothing.
    /// Returns false with a message naming the file when one cannot be read, is not a JSON
    /// object, or (the machine file only) could have been written by an account that is not
    /// an administrator.
    /// </summary>
    public bool TryAddTo(IConfigurationBuilder configuration, string environmentName, bool runningAsService, out string error)
    {
        error = string.Empty;
        var index = InsertionIndex(configuration.Sources, environmentName);

        foreach (var path in PathsFor(runningAsService))
        {
            if (!TryCheck(path, out var hasContent, out error))
            {
                return false;
            }

            if (!hasContent)
            {
                continue;
            }

            if (path == MachinePath && OperatingSystem.IsWindows() && MachineSettingsAccess.Check(path) is { } accessProblem)
            {
                error = accessProblem;
                return false;
            }

            var source = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = false };
            source.ResolveFileProvider();

            try
            {
                configuration.Sources.Insert(index++, source);
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or IOException)
            {
                // Checked above, so this is a race with an edit or a duplicate key, which the
                // configuration provider rejects but a JSON parser accepts.
                error = Describe(path, exception.InnerException?.Message ?? exception.Message);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Where the settings files go among the configuration sources: after the last of the JSON
    /// files the host reads from the content root, which are <c>appsettings.json</c>,
    /// <c>appsettings.{environmentName}.json</c>, <c>{ApplicationName}.settings.json</c> and
    /// <c>{ApplicationName}.settings.{environmentName}.json</c>, so the settings files rank above
    /// all four. Without any of them, ahead of the unprefixed environment variables and the
    /// command line; failing those, last. User secrets are not a content root file, so in
    /// Development they stay above the settings files.
    /// </summary>
    public static int InsertionIndex(IList<IConfigurationSource> sources, string environmentName)
    {
        for (var index = sources.Count - 1; index >= 0; index--)
        {
            if (sources[index] is JsonConfigurationSource { Path: { } path } && IsContentRootSettingsFile(path, environmentName))
            {
                return index + 1;
            }
        }

        for (var index = 0; index < sources.Count; index++)
        {
            if (sources[index] is CommandLineConfigurationSource
                || sources[index] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            {
                return index;
            }
        }

        return sources.Count;
    }

    /// <summary>
    /// Reads a settings file far enough to know it is usable: present with content, and a
    /// JSON object. A missing or blank file is usable and has no content.
    /// </summary>
    public static bool TryCheck(string path, out bool hasContent, out string error)
    {
        hasContent = false;
        error = string.Empty;

        string text;
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"The settings file {path} could not be read: {exception.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(text, ParseOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Describe(path, "the file must hold a single JSON object, { ... }.");
                return false;
            }
        }
        catch (JsonException exception)
        {
            error = Describe(path, exception.Message);
            return false;
        }

        hasContent = true;
        return true;
    }

    /// <summary>True for a file name the host reads from the content root for <paramref name="environmentName"/>.</summary>
    private static bool IsContentRootSettingsFile(string path, string environmentName)
    {
        if (string.Equals(path, "appsettings.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, $"appsettings.{environmentName}.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // {ApplicationName}.settings.json and {ApplicationName}.settings.{environmentName}.json.
        foreach (var suffix in (string[])[".settings.json", $".settings.{environmentName}.json"])
        {
            if (path.Length > suffix.Length
                && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && path.IndexOfAny(['/', '\\']) < 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(string path, string reason) =>
        $"The settings file {path} is not valid: {reason} Correct the file or delete it, then start again.";

    private static string KnownFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path) ? AppContext.BaseDirectory : path;
    }

    private static string RequireFullPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"'{path}' is not a fully qualified path.", parameterName);
        }

        return Path.GetFullPath(path);
    }
}
