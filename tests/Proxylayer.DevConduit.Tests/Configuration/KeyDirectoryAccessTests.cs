using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// The access list on the Data Protection key directory. Windows-only: every test is skipped
/// elsewhere. Only directories under a throwaway temp root are touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyDirectoryAccessTests
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, domainSid: null);

    private static void RequireWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory access lists are Windows-only.");

    private static FileSystemAccessRule[] Rules(FileSystemSecurity security) =>
        [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];

    private static DirectorySecurity AccessOf(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);

    private static HashSet<SecurityIdentifier> ExpectedAccounts()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return
        [
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null),
            identity.User!
        ];
    }

    [Fact]
    public void PermittedAccounts_AreSystemAdministratorsAndTheCurrentIdentity()
    {
        RequireWindows();

        Assert.True(ExpectedAccounts().SetEquals(KeyDirectoryAccess.PermittedAccounts()));
    }

    [Fact]
    public void Restrict_CreatesANewDirectoryWithOnlyThePermittedAccountsAndNoInheritance()
    {
        RequireWindows();
        using var root = new TempDirectory();
        var keys = new DirectoryInfo(Path.Combine(root.Path, "data", "keys"));

        Assert.Null(KeyDirectoryAccess.Restrict(keys));

        Assert.True(Directory.Exists(keys.FullName));
        var security = AccessOf(keys.FullName);
        var rules = Rules(security);

        Assert.True(security.AreAccessRulesProtected);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
        });
        Assert.True(ExpectedAccounts().SetEquals(rules.Select(rule => (SecurityIdentifier)rule.IdentityReference)));
        Assert.True(KeyDirectoryAccess.IsRestricted(security));
    }

    [Fact]
    public void IsRestricted_IsTrueForTheBuiltSecurity()
    {
        RequireWindows();

        Assert.True(KeyDirectoryAccess.IsRestricted(KeyDirectoryAccess.BuildSecurity()));
    }

    [Fact]
    public void IsRestricted_IsFalseWithAnEveryoneAllowRule()
    {
        RequireWindows();
        var security = KeyDirectoryAccess.BuildSecurity();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));

        Assert.False(KeyDirectoryAccess.IsRestricted(security));
    }

    [Fact]
    public void IsRestricted_IsFalseWhileInheritanceIsOn()
    {
        RequireWindows();
        var security = new DirectorySecurity();
        foreach (var account in KeyDirectoryAccess.PermittedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        Assert.False(security.AreAccessRulesProtected);
        Assert.False(KeyDirectoryAccess.IsRestricted(security));
    }

    [Fact]
    public void IsRestricted_AcceptsADenyRuleForAnyone()
    {
        RequireWindows();
        var security = KeyDirectoryAccess.BuildSecurity();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Write, AccessControlType.Deny));

        Assert.True(KeyDirectoryAccess.IsRestricted(security));
    }

    [Fact]
    public void IsRestricted_IsFalseForADirectoryThatInheritsFromTemp()
    {
        RequireWindows();
        using var root = new TempDirectory();

        Assert.False(KeyDirectoryAccess.IsRestricted(AccessOf(root.Path)));
    }

    [Fact]
    public void Restrict_TightensAnExistingBroadDirectoryAndTheFilesInIt()
    {
        RequireWindows();
        using var root = new TempDirectory();
        var keys = Directory.CreateDirectory(Path.Combine(root.Path, "keys"));

        var broad = keys.GetAccessControl(AccessControlSections.Access);
        broad.AddAccessRule(new FileSystemAccessRule(
            Everyone,
            FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        keys.SetAccessControl(broad);

        var keyFile = Path.Combine(keys.FullName, "key-existing.xml");
        File.WriteAllText(keyFile, "<key />");
        Assert.Contains(
            Rules(new FileInfo(keyFile).GetAccessControl(AccessControlSections.Access)),
            rule => rule.IdentityReference.Equals(Everyone));
        Assert.False(KeyDirectoryAccess.IsRestricted(AccessOf(keys.FullName)));

        Assert.Null(KeyDirectoryAccess.Restrict(keys));

        var tightened = AccessOf(keys.FullName);
        Assert.True(KeyDirectoryAccess.IsRestricted(tightened));
        Assert.DoesNotContain(Rules(tightened), rule => rule.IdentityReference.Equals(Everyone));
        Assert.DoesNotContain(
            Rules(new FileInfo(keyFile).GetAccessControl(AccessControlSections.Access)),
            rule => rule.IdentityReference.Equals(Everyone));
    }

    [Fact]
    public void Restrict_LeavesAnAlreadyRestrictedDirectoryAlone()
    {
        RequireWindows();
        using var root = new TempDirectory();
        var keys = new DirectoryInfo(Path.Combine(root.Path, "keys"));
        Assert.Null(KeyDirectoryAccess.Restrict(keys));

        Assert.Null(KeyDirectoryAccess.Restrict(new DirectoryInfo(keys.FullName)));
        Assert.True(KeyDirectoryAccess.IsRestricted(AccessOf(keys.FullName)));
    }

    [Fact]
    public void Restrict_ReturnsAWarningInsteadOfThrowingWhenTheDirectoryCannotBeCreated()
    {
        RequireWindows();
        using var root = new TempDirectory();

        // The key directory's parent is a file, so the directory can never be created.
        var blocker = Path.Combine(root.Path, "blocker");
        File.WriteAllText(blocker, string.Empty);
        var keys = new DirectoryInfo(Path.Combine(blocker, "keys"));

        var warning = KeyDirectoryAccess.Restrict(keys);

        Assert.NotNull(warning);
        Assert.StartsWith($"Could not limit the Data Protection key directory {keys.FullName} to SYSTEM, Administrators", warning);
        Assert.EndsWith("so restrict the directory by hand.", warning);
        Assert.False(Directory.Exists(keys.FullName));
    }
}
