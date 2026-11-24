using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Setup;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// <c>service-admin update &lt;transfer.msi&gt; [&lt;tray process id&gt;]</c>: installs an update the
/// workstation's tray downloaded. The package is copied out of the transfer file into the
/// restricted machine update folder and every check is made again on that copy, against
/// settings an unelevated account cannot change (the install folder's appsettings files and the
/// machine settings file): this must be an installed build; the package's properties must name
/// Dev.Conduit, newer than this version; the feed must publish that version, and the package's
/// SHA-256 must equal its checksum asset; its signature must match this exe's when this exe is
/// signed. Then the helper waits for the tray to exit, starts msiexec silently with the role,
/// environment and folder the MSI saved, and returns at once: msiexec replaces this very exe,
/// so the helper must not be running while it does. The tray's relauncher brings the tray back
/// when msiexec ends. Each refusal is appended to <c>update-helper.log</c> in the update folder.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpdateAdminAction
{
    public const string Action = "update";

    private const string HelperLogName = "update-helper.log";
    private static readonly TimeSpan TrayExitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The action's arguments: a transfer file (<see cref="UpdateTransfer.IsTransferPath"/>), and
    /// optionally the id of the tray process to wait for, a positive decimal number.
    /// </summary>
    public static bool TryParseArguments(IReadOnlyList<string> arguments, out string transferPath, out int? trayProcessId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        transferPath = string.Empty;
        trayProcessId = null;

        if (arguments.Count is < 1 or > 2 || !UpdateTransfer.IsTransferPath(arguments[0]))
        {
            return false;
        }

        if (arguments.Count == 2)
        {
            var text = arguments[1];
            if (text.Length is 0 or > 10 || !text.All(char.IsAsciiDigit)
                || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return false;
            }

            trayProcessId = id;
        }

        transferPath = arguments[0];
        return true;
    }

    public static ServiceAdminExitCode Run(IReadOnlyList<string> arguments)
    {
        if (!TryParseArguments(arguments, out var transferPath, out var trayProcessId))
        {
            return ServiceAdminExitCode.InvalidArguments;
        }

        var folder = UpdateFolder.Machine();
        try
        {
            var build = InstalledBuild.Detect();
            if (!build.IsInstalledBuild)
            {
                return Refuse(folder, $"Not an installed build: {build.Reason}.");
            }

            var transfer = new FileInfo(transferPath);
            if (!transfer.Exists || transfer.Attributes.HasFlag(FileAttributes.ReparsePoint) || transfer.Length > GitHubReleaseFeed.MaxMsiBytes)
            {
                return ServiceAdminExitCode.InvalidArguments;
            }

            folder.Ensure();
            var incoming = folder.IncomingPath();
            File.Copy(transferPath, incoming, overwrite: false);
            return Install(folder, incoming, build, trayProcessId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Append(folder, $"The package could not be copied into {folder.Path}: {exception.Message}");
            return ServiceAdminExitCode.UpdateFailed;
        }
        finally
        {
            UpdateTransfer.Delete(transferPath);
        }
    }

    private static ServiceAdminExitCode Install(UpdateFolder folder, string incoming, InstalledBuild build, int? trayProcessId)
    {
        try
        {
            var package = MsiPackageCheck.Evaluate(MsiPackageProperties.Read(incoming), ReleaseVersion.Current, expected: null);
            if (!package.Accepted)
            {
                return Refuse(folder, package.Reason);
            }

            var version = package.Version!;
            if (Checksum(version, out var problem) is not { } digest)
            {
                return Refuse(folder, problem);
            }

            var verification = UpdatePackageVerifier.ForThisProcess().Verify(incoming, digest, ReleaseVersion.Current, version);
            if (!verification.Accepted)
            {
                return Refuse(folder, verification.Reason);
            }

            var final = folder.PackagePath(version);
            File.Move(incoming, final, overwrite: true);
            folder.Prune(keep: final);

            if (!MsiexecCommand.TryBuild(final, folder.LogPath(version), build.Installer, out var command, out var error))
            {
                return Refuse(folder, error);
            }

            if (trayProcessId is { } id && !WaitForExit(id))
            {
                Append(folder, $"The tray (process {id}) did not exit within {TrayExitTimeout.TotalMinutes} minutes; nothing was installed.");
                return ServiceAdminExitCode.UpdateFailed;
            }

            using var process = command.Start();
            Append(folder, $"{verification.Reason} Started {command.FileName} {command.Arguments} (process {process.Id}).");
            return ServiceAdminExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            Append(folder, $"The update could not be started: {exception.Message}");
            return ServiceAdminExitCode.UpdateFailed;
        }
        finally
        {
            UpdateFolder.TryDelete(incoming);
        }
    }

    /// <summary>
    /// The SHA-256 the feed publishes for <paramref name="version"/>, from the feed the machine's
    /// own settings name. Null, with the reason, when updates are off there or the feed has no
    /// such release.
    /// </summary>
    private static byte[]? Checksum(Version version, out string problem)
    {
        var files = SettingsFiles.ForThisComputer();
        if (File.Exists(files.MachinePath) && MachineSettingsAccess.Check(files.MachinePath) is { } access)
        {
            problem = access;
            return null;
        }

        UpdateOptions options;
        try
        {
            options = UpdateOptions.From(
                new SettingsLayers(AppContext.BaseDirectory, SettingsLayers.ProcessEnvironmentName(), files).View(SettingsTarget.Machine).Effective);
        }
        catch (InvalidDataException exception)
        {
            problem = exception.Message;
            return null;
        }

        using var feed = GitHubReleaseFeed.Create(options, ReleaseVersion.Current, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, out var reason);
        if (feed is null)
        {
            problem = reason;
            return null;
        }

        try
        {
            var release = feed.FindReleaseAsync(version, CancellationToken.None).GetAwaiter().GetResult();
            if (release.Release is not { } found)
            {
                problem = release.Reason;
                return null;
            }

            var text = feed.DownloadTextAsync(found.ChecksumUrl, ChecksumFile.MaxBytes, CancellationToken.None).GetAwaiter().GetResult();
            if (!ChecksumFile.TryParse(text, found.MsiName, out var digest))
            {
                problem = $"{found.ChecksumName} is not a SHA-256 checksum of {found.MsiName}.";
                return null;
            }

            problem = string.Empty;
            return digest;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException or IOException)
        {
            problem = $"The release's checksum could not be fetched from {options.FeedUrl}: {exception.Message}";
            return null;
        }
    }

    private static bool WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(TrayExitTimeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static ServiceAdminExitCode Refuse(UpdateFolder folder, string reason)
    {
        Append(folder, $"Refused: {reason}");
        return ServiceAdminExitCode.UpdateRefused;
    }

    private static void Append(UpdateFolder folder, string line)
    {
        try
        {
            folder.Ensure();
            File.AppendAllText(folder.File(HelperLogName), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The exit code still says what happened.
        }
    }
}
