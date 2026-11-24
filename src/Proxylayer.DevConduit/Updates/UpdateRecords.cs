using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Updates;

/// <summary>An install that was started: what from, what to, when, and where msiexec logs it.</summary>
public sealed record UpdateAttempt(string Version, string FromVersion, DateTimeOffset StartedAt, string LogPath);

/// <summary>
/// What the updater remembers across restarts: the install in progress, so the next start can
/// tell whether it worked, and the last failed one, so it is retried at most once a day.
/// </summary>
public sealed record UpdateHistory(UpdateAttempt? Attempt, string? FailedVersion, DateTimeOffset? FailedAt, string? NotifiedVersion = null)
{
    public static UpdateHistory Empty { get; } = new(null, null, null);

    /// <summary>The earliest a retry of <paramref name="version"/> may start, or null when it has not failed.</summary>
    public DateTimeOffset? RetryNotBefore(Version version) =>
        FailedAt is { } failedAt && string.Equals(FailedVersion, ReleaseVersion.Format(version), StringComparison.Ordinal)
            ? failedAt + InstallWindow.RetryAfterFailure
            : null;

    /// <summary>What the previous start's attempt came to, now that this build runs.</summary>
    public UpdateOutcome Outcome(Version current)
    {
        if (Attempt is not { } attempt || !ReleaseVersion.TryParse(attempt.Version, out var target))
        {
            return UpdateOutcome.None;
        }

        return ReleaseVersion.Normalize(current) >= target ? UpdateOutcome.Installed : UpdateOutcome.Failed;
    }
}

public enum UpdateOutcome
{
    /// <summary>No install was started.</summary>
    None,

    /// <summary>The started install is the version now running.</summary>
    Installed,

    /// <summary>The started install did not take: the old version still runs.</summary>
    Failed
}

/// <summary>Reads and writes small JSON records next to the update folder. A missing or unreadable file reads as null.</summary>
public static class UpdateRecordFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T? Read<T>(string path)
        where T : class
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 64 * 1024 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the file in one step; false when it could not be written.</summary>
    public static bool Write<T>(string path, T value)
    {
        try
        {
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Delete(string path) => UpdateFolder.TryDelete(path);
}

/// <summary>
/// The service's update status, written for the tray on a server to show on its About page:
/// <c>%ProgramData%\Proxylayer\Dev.Conduit\update-status.json</c>, beside the machine settings file,
/// readable by users as that folder is. It holds versions and times only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceUpdateStatusFile
{
    public const string FileName = "update-status.json";

    public static string PathFor(SettingsFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Path.Combine(Path.GetDirectoryName(files.MachinePath)!, FileName);
    }

    public static UpdateStatus? Read(SettingsFiles files) => UpdateRecordFile.Read<UpdateStatus>(PathFor(files));

    public static void Write(SettingsFiles files, UpdateStatus status) => UpdateRecordFile.Write(PathFor(files), status);
}
