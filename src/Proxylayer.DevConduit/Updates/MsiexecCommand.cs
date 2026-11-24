using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// The silent upgrade command: <c>%SystemRoot%\System32\msiexec.exe /i "&lt;msi&gt;" /qn /norestart
/// /l*v "&lt;log&gt;" ROLE=… ENVIRONMENT=… INSTALLFOLDER="…"</c>, with the role, environment and
/// folder the MSI saved, so the upgrade keeps all three. Every value is checked before it reaches
/// the command line.
/// </summary>
public sealed partial record MsiexecCommand(string FileName, string Arguments)
{
    /// <summary>msiexec's exit code for success.</summary>
    public const int Success = 0;

    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED: installed, with a restart pending.</summary>
    public const int SuccessRebootRequired = 3010;

    /// <summary>msiexec in the system folder, found through the known folder rather than an environment variable.</summary>
    public static string MsiexecPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    public static bool Succeeded(int exitCode) => exitCode is Success or SuccessRebootRequired;

    /// <summary>
    /// The command for <paramref name="msiPath"/>, logging to <paramref name="logPath"/>, or false
    /// with the reason when a path or a saved value cannot be passed safely.
    /// </summary>
    public static bool TryBuild(
        string msiPath,
        string logPath,
        InstallerOwnership installer,
        [NotNullWhen(true)] out MsiexecCommand? command,
        out string error) =>
        TryBuild(MsiexecPath, msiPath, logPath, installer, out command, out error);

    public static bool TryBuild(
        string msiexecPath,
        string msiPath,
        string logPath,
        InstallerOwnership installer,
        [NotNullWhen(true)] out MsiexecCommand? command,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(installer);
        command = null;

        if (!IsSafePath(msiPath) || !msiPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{msiPath}' is not a fully qualified .msi path.";
            return false;
        }

        if (!IsSafePath(logPath))
        {
            error = $"'{logPath}' is not a fully qualified log path.";
            return false;
        }

        var role = installer.Role ?? InstallerOwnership.WorkstationRole;
        if (!string.Equals(role, InstallerOwnership.WorkstationRole, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, InstallerOwnership.ServerRole, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The saved role, '{role}', is not Workstation or Server.";
            return false;
        }

        var environment = installer.Environment;
        if (environment is not null && !ServiceAdminCommand.IsAcceptedEnvironmentName(environment))
        {
            error = $"The saved environment, '{environment}', is not an accepted environment name.";
            return false;
        }

        if (installer.InstallFolder is not { } folder || !IsSafePath(folder))
        {
            error = $"The saved install folder, '{installer.InstallFolder}', is not a fully qualified path.";
            return false;
        }

        var arguments = $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\" ROLE={Canonical(role)}";
        if (environment is not null)
        {
            arguments += $" ENVIRONMENT={environment}";
        }

        // Without its trailing separator: a backslash before the closing quote would escape it.
        arguments += $" INSTALLFOLDER=\"{InstalledBuild.Folder(folder)}\"";

        command = new MsiexecCommand(msiexecPath, arguments);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Starts msiexec without a window and without waiting. The child is not tied to this process:
    /// .NET puts it in no job object, so it keeps running when the service it was started from is
    /// stopped by the package's own ServiceControl.
    /// </summary>
    public Process Start()
    {
        var info = new ProcessStartInfo(FileName, Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(FileName)!
        };

        return Process.Start(info) ?? throw new InvalidOperationException("msiexec did not start.");
    }

    /// <summary>
    /// msiexec's exit code as its verbose log records it (<c>MainEngineThread is returning N</c>),
    /// or null when the log is missing or ends without it.
    /// </summary>
    public static int? ExitCodeFromLog(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return null;
            }

            byte[] content;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                content = buffer.ToArray();
            }

            // Windows Installer writes its log in the system code page or, on some systems, UTF-16.
            var text = content.Length > 1 && (content[1] == 0 || (content[0] == 0xFF && content[1] == 0xFE))
                ? System.Text.Encoding.Unicode.GetString(content)
                : System.Text.Encoding.UTF8.GetString(content);
            int? exitCode = null;
            foreach (Match match in ReturningLine().Matches(text))
            {
                exitCode = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return exitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }

    private static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && path.IndexOfAny(['"', '\r', '\n', '\0']) < 0;

    private static string Canonical(string role) =>
        string.Equals(role, InstallerOwnership.ServerRole, StringComparison.OrdinalIgnoreCase)
            ? InstallerOwnership.ServerRole
            : InstallerOwnership.WorkstationRole;

    [GeneratedRegex(@"MainEngineThread is returning (\d{1,10})", RegexOptions.CultureInvariant)]
    private static partial Regex ReturningLine();
}
