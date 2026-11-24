using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Who may write the machine settings file. The service reads it as LocalSystem, so a file
/// a standard user could write would let that user reconfigure the service: its listen
/// address, its certificate, its allow-list. Ordinary users can create files in a folder
/// under <c>%ProgramData%</c>, so the file is written with an access list of its own
/// (administrators and SYSTEM full control, users read) and every launch refuses a file
/// that anyone else owns or can write.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineSettingsAccess
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>The access list the elevated helper gives the file: not inherited, administrators and SYSTEM full control, users read.</summary>
    public static FileSecurity BuildSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            FileSystemRights.Read | FileSystemRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }

    /// <summary>
    /// Null when the file is owned by administrators, SYSTEM or TrustedInstaller and nobody
    /// else may write it; otherwise the reason it is refused, naming the file.
    /// </summary>
    public static string? Check(string path)
    {
        FileSecurity security;
        try
        {
            security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return $"The machine settings file {path} could not be checked: {exception.Message}";
        }

        return Check(path, security);
    }

    /// <summary>The same check against an access list already read.</summary>
    public static string? Check(string path, FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsAdministrative(owner))
        {
            return $"The machine settings file {path} is owned by {Describe(owner)}, not by Administrators or SYSTEM. " +
                "Every launch, including the service, reads this file, so it must be written by an administrator. " +
                "Save the settings again from the tray's setup window, which writes it elevated, or delete the file.";
        }

        var writers = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & WriteRights) != 0)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Where(account => !IsAdministrative(account))
            .Distinct()
            .ToList();

        if (writers.Count > 0)
        {
            return $"The machine settings file {path} can be changed by {string.Join(", ", writers.Select(Describe))}. " +
                "Every launch, including the service, reads this file, so only Administrators and SYSTEM may write it. " +
                "Save the settings again from the tray's setup window, which resets the file's permissions, or correct them by hand.";
        }

        return null;
    }

    private static bool IsAdministrative(SecurityIdentifier account) =>
        account.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || account.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || account == TrustedInstaller;

    private static string Describe(SecurityIdentifier? account)
    {
        if (account is null)
        {
            return "an unknown account";
        }

        try
        {
            return account.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return account.Value;
        }
    }
}
