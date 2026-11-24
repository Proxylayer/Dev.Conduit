using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// Where downloaded packages and install logs go. The machine folder,
/// <c>%ProgramData%\Proxylayer\Dev.Conduit\updates</c>, is limited to SYSTEM and Administrators with
/// inheritance off, as the Data Protection key folder is (<see cref="KeyDirectoryAccess"/>): the service downloads
/// there, and the elevated helper copies a package there before checking and installing it, so
/// nothing an unelevated account can write is ever handed to msiexec. The user folder,
/// <c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit\updates</c>, is where a workstation's tray downloads.
/// Only the latest package is kept.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateFolder
{
    public const string FolderName = "updates";

    private const string PackagePrefix = "Dev.Conduit-";
    private const string PartialSuffix = ".partial";

    private static readonly IReadOnlySet<SecurityIdentifier> PermittedAccounts = new HashSet<SecurityIdentifier>
    {
        new(WellKnownSidType.LocalSystemSid, domainSid: null),
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null)
    };

    public UpdateFolder(string path, bool restricted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"'{path}' is not a fully qualified path.", nameof(path));
        }

        Path = path;
        Restricted = restricted;
    }

    public string Path { get; }

    /// <summary>True for the machine folder, limited to SYSTEM and Administrators.</summary>
    public bool Restricted { get; }

    public static UpdateFolder Machine() =>
        new(System.IO.Path.Combine(KnownFolder(Environment.SpecialFolder.CommonApplicationData), SettingsFiles.FolderName, FolderName), restricted: true);

    public static UpdateFolder User() =>
        new(System.IO.Path.Combine(KnownFolder(Environment.SpecialFolder.LocalApplicationData), SettingsFiles.FolderName, FolderName), restricted: false);

    /// <summary>Where the package of <paramref name="version"/> is kept once verified.</summary>
    public string PackagePath(Version version) => System.IO.Path.Combine(Path, ReleaseAssetNames.Msi(version));

    /// <summary>A new name to download or copy to before the package is verified.</summary>
    public string PartialPath(Version version) =>
        System.IO.Path.Combine(Path, $"{ReleaseAssetNames.Msi(version)}.{Guid.NewGuid():N}{PartialSuffix}");

    /// <summary>A new name for a package whose version is not known yet, such as one the helper was handed.</summary>
    public string IncomingPath() => System.IO.Path.Combine(Path, $"{PackagePrefix}incoming.{Guid.NewGuid():N}{PartialSuffix}");

    /// <summary>msiexec's verbose log for installing <paramref name="version"/>.</summary>
    public string LogPath(Version version) => System.IO.Path.Combine(Path, $"install-{ReleaseVersion.Format(version)}.log");

    /// <summary>A file in this folder, by name.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>
    /// Creates the folder, the machine folder with its restricted access list in one step, and
    /// tightens an existing machine folder whose list is broader.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder could not be created or restricted.</exception>
    public void Ensure()
    {
        var directory = new DirectoryInfo(Path);
        if (!Restricted)
        {
            directory.Create();
            return;
        }

        if (!directory.Exists)
        {
            directory.Parent?.Create();
            directory.Create(BuildSecurity());
            return;
        }

        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{Path} is a link, not a folder.");
        }

        if (!IsRestricted(directory.GetAccessControl(AccessControlSections.Access)))
        {
            directory.SetAccessControl(BuildSecurity());
        }
    }

    /// <summary>
    /// Full control for SYSTEM and Administrators only, not inherited. Unlike the key folder the
    /// process's own account is not added: the elevated helper runs as the signed-in user, whose
    /// unelevated processes must not be able to write here.
    /// </summary>
    public static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in PermittedAccounts)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>True when the list does not inherit and allows only SYSTEM and Administrators.</summary>
    public static bool IsRestricted(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        return security.AreAccessRulesProtected
            && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .All(rule => rule.AccessControlType == AccessControlType.Deny
                    || PermittedAccounts.Contains((SecurityIdentifier)rule.IdentityReference));
    }

    /// <summary>Deletes every package and partial download except <paramref name="keep"/>.</summary>
    public void Prune(string? keep)
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Path, PackagePrefix + "*"))
        {
            var name = System.IO.Path.GetFileName(file);
            var isPackage = name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase);
            if (isPackage && !string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    public static void TryDelete(string path)
    {
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
            // Pruned again after the next download.
        }
    }

    private static string KnownFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrEmpty(path) ? System.IO.Path.GetTempPath() : path;
    }
}
