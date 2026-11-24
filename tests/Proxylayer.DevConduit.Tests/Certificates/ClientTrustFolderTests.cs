using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// One server's client folder and the directory holding them, under a throwaway root: the
/// pinned certificates in <c>trust.json</c> and the client identity, machine-DPAPI protected.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientTrustFolderTests : IDisposable
{
    private const string Host = "devconduit.example.internal";

    private readonly TempDirectory _root = new();
    private readonly ClientCertificateDirectory _directory;

    public ClientTrustFolderTests()
    {
        _directory = new ClientCertificateDirectory(Path.Combine(_root.Path, "client"));
    }

    public void Dispose() => _root.Dispose();

    // ---- ClientCertificateDirectory ------------------------------------------------------

    [Fact]
    public void Directory_NamesItsRecordFilesBesideTheFolders()
    {
        var root = Path.Combine(_root.Path, "client");

        Assert.Equal(root, _directory.Root);
        Assert.Equal(Path.Combine(root, "root-store.json"), _directory.RootStoreRecordPath);
        Assert.Equal(Path.Combine(root, "imports.json"), _directory.ImportRecordPath);
        Assert.Equal(Path.Combine(root, Host), _directory.Folder(Host).Directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\client")]
    public void Directory_RefusesARootThatIsNotFullyQualified(string root) =>
        Assert.ThrowsAny<ArgumentException>(() => new ClientCertificateDirectory(root));

    [Fact]
    public void ForThisComputer_IsTheClientFolderBesideTheMachineSettings()
    {
        // Only the path is computed; nothing is read or created there.
        var directory = ClientCertificateDirectory.ForThisComputer();

        Assert.Equal("client", Path.GetFileName(directory.Root));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), directory.Root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folders_ListsOnlyFoldersNamedForHosts()
    {
        Directory.CreateDirectory(Path.Combine(_directory.Root, Host));
        Directory.CreateDirectory(Path.Combine(_directory.Root, "b.example"));
        Directory.CreateDirectory(Path.Combine(_directory.Root, "not_a_host"));
        File.WriteAllText(Path.Combine(_directory.Root, "imports.json"), "[]");

        Assert.Equal(["b.example", Host], _directory.Folders().Select(folder => folder.Hostname).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Folders_OfAMissingRoot_IsEmpty() =>
        Assert.Empty(_directory.Folders());

    // ---- ClientTrustFolder ---------------------------------------------------------------

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Folder_RefusesANameThatIsNotAHost(string hostname) =>
        Assert.Throws<ArgumentException>(() => _directory.Folder(hostname));

    [Fact]
    public void Folder_NormalizesTheHostname()
    {
        var folder = _directory.Folder(" DevConduit.Example.Internal. ");

        Assert.Equal(Host, folder.Hostname);
        Assert.Equal(Path.Combine(_directory.Root, Host, "trust.json"), folder.TrustPath);
        Assert.Equal(Path.Combine(_directory.Root, Host, "identity.pfx"), folder.IdentityPath);
    }

    [Fact]
    public void ReadTrust_OfAMissingFile_IsEmpty() =>
        Assert.Empty(_directory.Folder(Host).ReadTrust());

    [Fact]
    public void WriteTrustThenReadTrust_RoundTripsThePins()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);
        var folder = _directory.Folder(Host);
        var pins = new[] { PinnedTrust.Pin(certificate) };

        folder.WriteTrust(pins);

        Assert.Equal(pins, folder.ReadTrust());
        var text = File.ReadAllText(folder.TrustPath);
        Assert.Contains($"\"hostname\": \"{Host}\"", text);
        Assert.Contains("\"certificates\"", text);
        Assert.Single(Directory.GetFiles(folder.Directory));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("""{ "hostname": "other.example", "certificates": [ { "der": "AA==", "notBefore": "2026-01-01T00:00:00Z", "notAfter": "2027-01-01T00:00:00Z", "thumbprint": "AA" } ] }""")]
    public void ReadTrust_OfADamagedOrForeignFile_IsEmpty(string text)
    {
        var folder = _directory.Folder(Host);
        Directory.CreateDirectory(folder.Directory);
        File.WriteAllText(folder.TrustPath, text);

        Assert.Empty(folder.ReadTrust());
    }

    [Fact]
    public void ReadIdentity_WithoutAFile_IsNull()
    {
        var folder = _directory.Folder(Host);

        Assert.False(folder.HasIdentity);
        Assert.Null(folder.ReadIdentity());
    }

    [Fact]
    public void WriteIdentityThenReadIdentity_RoundTripsTheCertificateAndKey()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, TestCertificates.Now);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, TestCertificates.Now, TestCertificates.Enrollment), TestCertificates.Now);
        var folder = _directory.Folder(Host);

        folder.WriteIdentity(CertificateFactory.ExportPkcs12(client));

        Assert.True(folder.HasIdentity);
        using var read = folder.ReadIdentity();
        Assert.NotNull(read);
        Assert.Equal(client.Thumbprint, read.Thumbprint);
        Assert.True(read.HasPrivateKey);
    }

    [Fact]
    public void Identity_IsMachineDpapiOnDiskNotPlainPkcs12()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, TestCertificates.Now);
        using var client = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, TestCertificates.Now, TestCertificates.Enrollment), TestCertificates.Now);
        var folder = _directory.Folder(Host);
        var pkcs12 = CertificateFactory.ExportPkcs12(client);

        folder.WriteIdentity(pkcs12);
        var onDisk = File.ReadAllBytes(folder.IdentityPath);

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(onDisk, password: null, X509KeyStorageFlags.EphemeralKeySet));
        Assert.Equal(pkcs12, DpapiProtectedFile.Read(folder.IdentityPath));
    }

    [Fact]
    public void WriteIdentity_ReplacesAnEarlierOne()
    {
        using var authority = CertificateFactory.CreateClientAuthority(Host, TestCertificates.Now);
        using var first = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, TestCertificates.Now, TestCertificates.Enrollment), TestCertificates.Now);
        using var second = CertificateFactory.CreateClientCertificate(authority, CertificateFactory.ClientSubject(Host, TestCertificates.Now, TestCertificates.Enrollment), TestCertificates.Now);
        var folder = _directory.Folder(Host);

        folder.WriteIdentity(CertificateFactory.ExportPkcs12(first));
        folder.WriteIdentity(CertificateFactory.ExportPkcs12(second));

        using var read = folder.ReadIdentity();
        Assert.Equal(second.Thumbprint, read!.Thumbprint);
        Assert.Single(Directory.GetFiles(folder.Directory));
    }

    /// <summary>
    /// Gives the current user full control of a folder the machine layer's access list locked
    /// to Administrators, so the throwaway root can be deleted. As its owner, the user may.
    /// </summary>
    private static void Release(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [Fact]
    public void Create_GivesTheFolderItsOwnProtectedAccessList()
    {
        var folder = _directory.Folder(Host);

        folder.Create();

        try
        {
            var rules = new DirectoryInfo(folder.Directory).GetAccessControl(AccessControlSections.Access);
            Assert.True(rules.AreAccessRulesProtected);
            var granted = rules.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .ToDictionary(rule => ((SecurityIdentifier)rule.IdentityReference).Value, rule => rule.FileSystemRights);
            Assert.Equal(
                new[]
                {
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
                }.Order(StringComparer.Ordinal),
                granted.Keys.Order(StringComparer.Ordinal));
            var users = granted[new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value];
            Assert.True(users.HasFlag(FileSystemRights.ReadAndExecute));
            Assert.False(users.HasFlag(FileSystemRights.WriteData));
            Assert.Equal(FileSystemRights.FullControl, granted[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value]);
        }
        finally
        {
            Release(folder.Directory);
        }
    }

    [Fact]
    public void Create_OnAnExistingFolder_ResetsItsAccessList()
    {
        var folder = _directory.Folder(Host);
        Directory.CreateDirectory(folder.Directory);
        Assert.False(new DirectoryInfo(folder.Directory).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);

        folder.Create();

        try
        {
            Assert.True(new DirectoryInfo(folder.Directory).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
        }
        finally
        {
            Release(folder.Directory);
        }
    }

    [Fact]
    public void Create_WithoutRestriction_LeavesTheInheritedAccessList()
    {
        var folder = new ClientCertificateDirectory(_directory.Root, restrictFolders: false).Folder(Host);

        folder.Create();

        Assert.False(new DirectoryInfo(folder.Directory).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
    }

    [Fact]
    public void BuildSecurity_GivesUsersReadOnly()
    {
        var users = ClientTrustFolder.BuildSecurity()
            .GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));

        Assert.True(users.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.AppendData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.Delete));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.ChangePermissions));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.TakeOwnership));
    }

    [Fact]
    public void BuildSecurity_IsProtectedAndGivesAdministratorsAndSystemFullControl()
    {
        var security = ClientTrustFolder.BuildSecurity();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) && rule.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) && rule.FileSystemRights == FileSystemRights.FullControl);
    }

    // ---- IsRestricted and Tighten ------------------------------------------------------------

    private static FileSystemAccessRule Allow(WellKnownSidType sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);

    [Fact]
    public void IsRestricted_IsTrueForTheBuiltSecurity() =>
        Assert.True(ClientTrustFolder.IsRestricted(ClientTrustFolder.BuildSecurity()));

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.CreateFiles)]
    [InlineData(WellKnownSidType.WorldSid, FileSystemRights.Read)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid, FileSystemRights.ReadAndExecute)]
    public void IsRestricted_IsFalseWithABroaderAllowRule(WellKnownSidType sid, FileSystemRights rights)
    {
        var security = ClientTrustFolder.BuildSecurity();
        security.AddAccessRule(Allow(sid, rights));

        Assert.False(ClientTrustFolder.IsRestricted(security));
    }

    [Fact]
    public void IsRestricted_IsFalseWhileInheritanceIsOn()
    {
        var security = ClientTrustFolder.BuildSecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

        Assert.False(ClientTrustFolder.IsRestricted(security));
    }

    [Fact]
    public void IsRestricted_AcceptsADenyRuleAndANarrowerList()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Deny));

        Assert.True(ClientTrustFolder.IsRestricted(security));
    }

    [Fact]
    public void Tighten_ReplacesABroadListAndThenLeavesItAlone()
    {
        var folder = _directory.Folder(Host);
        var directory = Directory.CreateDirectory(folder.Directory);
        var broad = directory.GetAccessControl(AccessControlSections.Access);
        broad.AddAccessRule(Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify));
        directory.SetAccessControl(broad);

        try
        {
            Assert.True(folder.Tighten());
            Assert.True(ClientTrustFolder.IsRestricted(directory.GetAccessControl(AccessControlSections.Access)));
            Assert.False(folder.Tighten());
        }
        finally
        {
            Release(folder.Directory);
        }
    }

    [Fact]
    public void Tighten_LeavesAMissingFolderAndAnUnrestrictedOneAlone()
    {
        Assert.False(_directory.Folder(Host).Tighten());
        Assert.False(Directory.Exists(_directory.Folder(Host).Directory));

        var unrestricted = new ClientCertificateDirectory(_directory.Root, restrictFolders: false).Folder(Host);
        Directory.CreateDirectory(unrestricted.Directory);
        Assert.False(unrestricted.Tighten());
        Assert.False(new DirectoryInfo(unrestricted.Directory).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
    }

    // ---- ClientTrustStore ----------------------------------------------------------------

    [Fact]
    public void Store_PinsFor_ReadsTheFolderAndIsEmptyForUnknownHosts()
    {
        using var certificate = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);
        _directory.Folder(Host).WriteTrust([PinnedTrust.Pin(certificate)]);
        var store = new ClientTrustStore(ClientTrustLayers.MachineOnly(_directory));

        Assert.Equal(certificate.Thumbprint, Assert.Single(store.PinsFor(Host.ToUpperInvariant())).Thumbprint);
        Assert.Empty(store.PinsFor("other.example"));
        Assert.Empty(store.PinsFor("10.0.0.5"));
        Assert.Same(_directory, store.Layers.Machine);
    }
}
