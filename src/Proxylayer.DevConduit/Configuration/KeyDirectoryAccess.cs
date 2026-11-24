using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Proxylayer.DevConduit.Configuration;

/// <summary>
/// Limits the Data Protection key directory to SYSTEM, BUILTIN\Administrators and the account
/// this process runs as, with inheritance from the data root turned off. Keys protected with
/// machine-scope DPAPI can be decrypted by any local account that can read them, so the
/// directory's access list is what keeps them private.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyDirectoryAccess
{
    private const InheritanceFlags Inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Creates the directory with the restricted access list, or tightens an existing
    /// directory whose list is broader. Returns a warning when that could not be done; the
    /// caller logs it and carries on, since the keys still work with a broader list.
    /// </summary>
    public static string? Restrict(DirectoryInfo directory)
    {
        try
        {
            if (!directory.Exists)
            {
                // Created with its access list in one step, so there is no moment at which the
                // directory exists with the data root's inherited permissions.
                if (directory.Parent is { } parent)
                {
                    parent.Create();
                }

                directory.Create(BuildSecurity());
                return null;
            }

            if (IsRestricted(directory.GetAccessControl(AccessControlSections.Access)))
            {
                return null;
            }

            // Only the access list is written, so the owner is left alone. Windows re-applies
            // the new inheritable entries to key files already in the directory.
            directory.SetAccessControl(BuildSecurity());
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException or InvalidOperationException)
        {
            return $"Could not limit the Data Protection key directory {directory.FullName} to SYSTEM, Administrators and " +
                $"this process's account: {exception.Message} Any local account that can read the key files can decrypt " +
                "them, so restrict the directory by hand.";
        }
    }

    /// <summary>
    /// True when the access list does not inherit from the parent and allows nobody outside
    /// the permitted accounts. Deny entries narrow access, so they are accepted as they are.
    /// </summary>
    public static bool IsRestricted(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var permitted = PermittedAccounts();
        return security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType == AccessControlType.Deny
                || permitted.Contains((SecurityIdentifier)rule.IdentityReference));
    }

    /// <summary>Full control for each permitted account, applied to the directory and everything in it.</summary>
    public static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var account in PermittedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.FullControl,
                Inherited,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>SYSTEM, BUILTIN\Administrators and the current process identity; two entries when running as SYSTEM.</summary>
    public static IReadOnlySet<SecurityIdentifier> PermittedAccounts()
    {
        using var identity = WindowsIdentity.GetCurrent();

        var accounts = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, domainSid: null),
            new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null)
        };

        if (identity.User is { } current)
        {
            accounts.Add(current);
        }

        return accounts;
    }
}
