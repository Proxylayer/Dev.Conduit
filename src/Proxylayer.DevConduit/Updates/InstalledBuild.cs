using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// Whether this process is a build the MSI installed, the only kind the updater ever touches:
/// the MSI is installed, it saved its install folder, and the running exe is in that folder.
/// A development build, a zip folder or a copy elsewhere is never updated.
/// </summary>
/// <param name="IsInstalledBuild">True when updates may be checked for and installed.</param>
/// <param name="Installer">The MSI installation, with the role, environment and folder an upgrade passes on.</param>
/// <param name="Reason">Why updates are off; empty when they are on.</param>
public sealed record InstalledBuild(bool IsInstalledBuild, InstallerOwnership Installer, string Reason)
{
    public const string ExeName = "Dev.Conduit.exe";

    /// <summary>The installed exe, when this is an installed build.</summary>
    public string? ExePath => IsInstalledBuild ? Path.Combine(Installer.InstallFolder!, ExeName) : null;

    [SupportedOSPlatform("windows")]
    public static InstalledBuild Detect() => Evaluate(InstallerOwnership.Detect(), Environment.ProcessPath);

    /// <summary>Pure: the gate for <paramref name="installer"/> and the running exe at <paramref name="processPath"/>.</summary>
    public static InstalledBuild Evaluate(InstallerOwnership installer, string? processPath)
    {
        ArgumentNullException.ThrowIfNull(installer);

        if (!installer.IsInstalled)
        {
            return new InstalledBuild(false, installer, "the Dev.Conduit MSI is not installed");
        }

        if (string.IsNullOrWhiteSpace(installer.InstallFolder) || !Path.IsPathFullyQualified(installer.InstallFolder))
        {
            return new InstalledBuild(false, installer, "the MSI saved no install folder; install a version that saves it");
        }

        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            return new InstalledBuild(false, installer, "the running exe's path is unknown");
        }

        if (!string.Equals(Path.GetFileName(processPath), ExeName, StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledBuild(false, installer, $"the running exe is not {ExeName}");
        }

        var running = Folder(Path.GetDirectoryName(processPath)!);
        var installed = Folder(installer.InstallFolder);
        return string.Equals(running, installed, StringComparison.OrdinalIgnoreCase)
            ? new InstalledBuild(true, installer, string.Empty)
            : new InstalledBuild(false, installer, $"the running exe is in {running}, not the MSI's install folder {installed}");
    }

    /// <summary>A folder path without its trailing separator, for comparison.</summary>
    public static string Folder(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
