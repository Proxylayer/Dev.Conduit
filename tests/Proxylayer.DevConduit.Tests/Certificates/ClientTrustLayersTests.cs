using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The per-user overlay of a server's client trust, under a throwaway root standing in for both
/// <c>%ProgramData%</c> and <c>%LOCALAPPDATA%</c>: effective pins are both layers together, the
/// effective identity is the user's renewal while it is valid and of the machine's enrollment,
/// the user layer is DPAPI-protected for the user and private to them, and removing it leaves
/// the machine layer alone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientTrustLayersTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Other = "fedcba9876543210fedcba9876543210";
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    private readonly TempDirectory _root = new();
    private readonly ClientTrustLayers _layers;
    private readonly X509Certificate2 _authority = CertificateFactory.CreateClientAuthority(Host, Now.AddDays(-500));
    private readonly X509Certificate2 _current = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-100));
    private readonly X509Certificate2 _next = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-10));

    public ClientTrustLayersTests()
    {
        _layers = new ClientTrustLayers(
            new ClientCertificateDirectory(Path.Combine(_root.Path, "machine", "client"), restrictFolders: false),
            Path.Combine(_root.Path, "user", "client"));
    }

    public void Dispose()
    {
        _authority.Dispose();
        _current.Dispose();
        _next.Dispose();
        _root.Dispose();
    }

    private X509Certificate2 Client(DateTimeOffset issuedAt, string enrollment = TestCertificates.Enrollment) =>
        CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, issuedAt, enrollment), issuedAt);

    private void MachineIdentity(X509Certificate2 certificate) =>
        _layers.MachineFolder(Host).WriteIdentity(CertificateFactory.ExportPkcs12(certificate));

    private void UserIdentity(X509Certificate2 certificate) =>
        _layers.UserFolder(Host)!.WriteIdentity(CertificateFactory.ExportPkcs12(certificate));

    // ---- Effective pins --------------------------------------------------------------------

    [Fact]
    public void Union_IsEveryPinOfBothLayersOncePerThumbprint()
    {
        var machine = PinnedTrust.Pin(_current);
        var user = PinnedTrust.Pin(_next);

        var union = PinnedTrust.Union([machine], [user, machine with { Thumbprint = machine.Thumbprint.ToLowerInvariant() }]);

        Assert.Equal([machine.Thumbprint, user.Thumbprint], union.Select(pin => pin.Thumbprint));
    }

    [Fact]
    public void Union_KeepsExpiredPins()
    {
        var expired = PinnedTrust.Pin(_current) with { NotAfter = Now.AddDays(-1) };

        Assert.Single(PinnedTrust.Union([expired], []));
    }

    [Fact]
    public void Pins_AreTheMachinePinsAndTheUsersTogether()
    {
        _layers.MachineFolder(Host).WriteTrust([PinnedTrust.Pin(_current)]);
        _layers.UserFolder(Host)!.WriteTrust([PinnedTrust.Pin(_next)]);

        Assert.Equal(
            new[] { _current.Thumbprint, _next.Thumbprint }.Order(StringComparer.Ordinal),
            _layers.Pins(Host).Select(pin => pin.Thumbprint).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MachineOnly_NeverReadsTheUserLayer()
    {
        _layers.MachineFolder(Host).WriteTrust([PinnedTrust.Pin(_current)]);
        _layers.UserFolder(Host)!.WriteTrust([PinnedTrust.Pin(_next)]);

        var machineOnly = ClientTrustLayers.MachineOnly(_layers.Machine);

        Assert.Null(machineOnly.UserFolder(Host));
        Assert.Equal([_current.Thumbprint], machineOnly.Pins(Host).Select(pin => pin.Thumbprint));
    }

    [Fact]
    public void Store_AcceptsAPinFromEitherLayerAndSeesTheUserLayerChange()
    {
        _layers.MachineFolder(Host).WriteTrust([PinnedTrust.Pin(_current)]);
        var store = new ClientTrustStore(_layers);
        var policy = new RemoteCertificatePolicy(store, new ManualClock(Now));
        Assert.Single(store.PinsFor(Host));
        Assert.False(policy.Accepts(Host, _next, SslPolicyErrors.RemoteCertificateChainErrors));

        _layers.UserFolder(Host)!.WriteTrust([PinnedTrust.Pin(_next)]);
        File.SetLastWriteTimeUtc(_layers.UserFolder(Host)!.TrustPath, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(2, store.PinsFor(Host).Count);
        Assert.True(policy.Accepts(Host, _current, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(policy.Accepts(Host, _next, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    // ---- Effective identity ----------------------------------------------------------------

    [Fact]
    public void Identity_WithoutAUserLayer_IsTheMachines()
    {
        using var machine = Client(Now.AddDays(-300));
        MachineIdentity(machine);

        using var chosen = _layers.Identity(Host, Now);

        Assert.Equal(machine.Thumbprint, chosen!.Thumbprint);
    }

    [Fact]
    public void Identity_PrefersTheUsersValidRenewalOfTheSameEnrollment()
    {
        using var machine = Client(Now.AddDays(-300));
        using var renewal = Client(Now.AddDays(-1));
        MachineIdentity(machine);
        UserIdentity(renewal);

        using var chosen = _layers.Identity(Host, Now);

        Assert.Equal(renewal.Thumbprint, chosen!.Thumbprint);
        Assert.True(chosen.HasPrivateKey);
    }

    [Fact]
    public void Identity_FallsBackToTheMachinesWhenTheUsersHasExpired()
    {
        using var machine = Client(Now.AddDays(-10));
        using var stale = Client(Now.AddDays(-400));
        MachineIdentity(machine);
        UserIdentity(stale);

        using var chosen = _layers.Identity(Host, Now);

        Assert.Equal(machine.Thumbprint, chosen!.Thumbprint);
    }

    [Fact]
    public void Identity_IgnoresAUserRenewalOfAnotherEnrollment()
    {
        // A new .conduit was imported since: the user's renewal belongs to the old enrollment.
        using var machine = Client(Now.AddDays(-10));
        using var old = Client(Now.AddDays(-1), Other);
        MachineIdentity(machine);
        UserIdentity(old);

        using var chosen = _layers.Identity(Host, Now);

        Assert.Equal(machine.Thumbprint, chosen!.Thumbprint);
    }

    [Fact]
    public void Identity_WithoutAMachineIdentity_IsNoneEvenWithAUserOne()
    {
        using var renewal = Client(Now.AddDays(-1));
        UserIdentity(renewal);

        Assert.False(_layers.HasIdentity(Host));
        Assert.Null(_layers.Identity(Host, Now));
    }

    [Fact]
    public void Identity_WithADamagedUserFile_IsTheMachines()
    {
        using var machine = Client(Now.AddDays(-10));
        MachineIdentity(machine);
        Directory.CreateDirectory(_layers.UserFolder(Host)!.Directory);
        File.WriteAllBytes(_layers.UserFolder(Host)!.IdentityPath, [1, 2, 3]);

        using var chosen = _layers.Identity(Host, Now);

        Assert.Equal(machine.Thumbprint, chosen!.Thumbprint);
    }

    [Fact]
    public void Choose_WithoutAnEnrollmentInTheMachineIdentity_KeepsTheMachines()
    {
        var subject = new X500DistinguishedName($"CN={Host} client");
        var machine = CertificateFactory.CreateClientCertificate(_authority, subject, Now.AddDays(-10));
        var user = CertificateFactory.CreateClientCertificate(_authority, subject, Now.AddDays(-1));

        using var chosen = ClientTrustLayers.Choose(machine, user, Now);

        Assert.Same(machine, chosen);
    }

    // ---- Machine identity renewal ------------------------------------------------------------

    [Fact]
    public void MachineIdentityRenewalDue_IsFalseWithMoreThanSixtyDaysLeft()
    {
        using var machine = Client(Now.AddDays(-300));
        MachineIdentity(machine);

        Assert.False(_layers.MachineIdentityRenewalDue(Host, Now));
    }

    [Fact]
    public void MachineIdentityRenewalDue_IsTrueInsideTheRenewalWindow()
    {
        using var machine = Client(Now.AddDays(-320));
        MachineIdentity(machine);

        Assert.True(_layers.MachineIdentityRenewalDue(Host, Now));
    }

    [Fact]
    public void MachineIdentityRenewalDue_IgnoresAUsersFreshRenewal()
    {
        using var machine = Client(Now.AddDays(-320));
        using var user = Client(Now.AddDays(-1));
        MachineIdentity(machine);
        UserIdentity(user);

        Assert.True(_layers.MachineIdentityRenewalDue(Host, Now));
    }

    [Fact]
    public void MachineIdentityRenewalDue_IsFalseOnceExpiredOrWithoutAnIdentity()
    {
        Assert.False(_layers.MachineIdentityRenewalDue(Host, Now));

        using var machine = Client(Now.AddDays(-400));
        MachineIdentity(machine);

        Assert.False(_layers.MachineIdentityRenewalDue(Host, Now));
    }

    [Fact]
    public void MachineIdentityRenewalDue_WithADamagedFile_IsFalse()
    {
        Directory.CreateDirectory(_layers.MachineFolder(Host).Directory);
        File.WriteAllBytes(_layers.MachineFolder(Host).IdentityPath, [1, 2, 3]);

        Assert.False(_layers.MachineIdentityRenewalDue(Host, Now));
    }

    // ---- The user layer on disk --------------------------------------------------------------

    [Fact]
    public void UserIdentity_IsUserDpapiOnDiskNotMachineDpapi()
    {
        using var renewal = Client(Now.AddDays(-1));
        var pkcs12 = CertificateFactory.ExportPkcs12(renewal);
        var folder = _layers.UserFolder(Host)!;

        folder.WriteIdentity(pkcs12);

        Assert.Equal(ClientTrustLayer.User, folder.Layer);
        Assert.Equal(pkcs12, DpapiProtectedFile.Read(folder.IdentityPath, DataProtectionScope.CurrentUser));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(folder.IdentityPath), null));

        // A DPAPI blob records its scope: CRYPTPROTECT_LOCAL_MACHINE (4) in the flags at offset 40.
        _layers.MachineFolder(Host).WriteIdentity(pkcs12);
        Assert.Equal(0u, BitConverter.ToUInt32(File.ReadAllBytes(folder.IdentityPath), 40) & 4);
        Assert.Equal(4u, BitConverter.ToUInt32(File.ReadAllBytes(_layers.MachineFolder(Host).IdentityPath), 40) & 4);
    }

    [Fact]
    public void UserFolder_IsUnderTheUserRootNamedForTheHost()
    {
        var folder = _layers.UserFolder(" DevConduit.Example.Internal. ")!;

        Assert.Equal(Path.Combine(_root.Path, "user", "client", Host), folder.Directory);
    }

    [Fact]
    public void ForThisUser_IsTheMachineDirectoryAndLocalAppData()
    {
        // Only the paths are computed; nothing is read or created there.
        var layers = ClientTrustLayers.ForThisUser();

        Assert.Equal(ClientCertificateDirectory.ForThisComputer().Root, layers.Machine.Root);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Proxylayer", "Dev.Conduit", "client"),
            layers.UserRoot);
    }

    [Fact]
    public void Create_InAProfileFolder_KeepsTheInheritedListThatReachesNoOtherUser()
    {
        var folder = _layers.UserFolder(Host)!;

        folder.Create();

        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectoryInfo(folder.Directory).GetAccessControl(AccessControlSections.Access);
        Assert.True(ClientTrustFolder.IsPrivateTo(security, identity.User!));
    }

    [Fact]
    public void Create_UnderAFolderUsersMayRead_MakesTheFolderPrivate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userRoot = Path.Combine(_root.Path, "shared", "client");
        Directory.CreateDirectory(userRoot);
        var broad = new DirectoryInfo(userRoot).GetAccessControl(AccessControlSections.Access);
        broad.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(userRoot).SetAccessControl(broad);
        var folder = new ClientTrustLayers(_layers.Machine, userRoot).UserFolder(Host)!;

        folder.Create();

        var security = new DirectoryInfo(folder.Directory).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(ClientTrustFolder.IsPrivateTo(security, identity.User!));
        folder.WriteTrust([PinnedTrust.Pin(_current)]);
        Assert.Single(folder.ReadTrust());
    }

    [Fact]
    public void IsPrivateTo_RefusesAReadRuleForAnyoneElse()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = ClientTrustFolder.BuildPrivateSecurity(identity.User!);
        Assert.True(ClientTrustFolder.IsPrivateTo(security, identity.User!));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadData, AccessControlType.Allow));

        Assert.False(ClientTrustFolder.IsPrivateTo(security, identity.User!));
    }

    [Fact]
    public void IsPrivateTo_IgnoresADenyRuleAndARuleWithoutReading()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = ClientTrustFolder.BuildPrivateSecurity(identity.User!);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadData, AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Synchronize, AccessControlType.Allow));

        Assert.True(ClientTrustFolder.IsPrivateTo(security, identity.User!));
    }

    // ---- Removing the user layer -------------------------------------------------------------

    [Fact]
    public void ForgetUser_DeletesTheUsersFolderAndLeavesTheMachineLayer()
    {
        using var machine = Client(Now.AddDays(-10));
        using var renewal = Client(Now.AddDays(-1));
        MachineIdentity(machine);
        _layers.MachineFolder(Host).WriteTrust([PinnedTrust.Pin(_current)]);
        UserIdentity(renewal);
        _layers.UserFolder(Host)!.WriteTrust([PinnedTrust.Pin(_next)]);

        var line = _layers.ForgetUser(Host);

        Assert.Contains("Removed", line);
        Assert.False(Directory.Exists(_layers.UserFolder(Host)!.Directory));
        Assert.True(_layers.MachineFolder(Host).HasIdentity);
        Assert.Equal([_current.Thumbprint], _layers.Pins(Host).Select(pin => pin.Thumbprint));
    }

    [Fact]
    public void ForgetUser_WithoutAUserFolderOrLayer_ChangesNothing()
    {
        Assert.Contains("No client folder", _layers.ForgetUser(Host));
        Assert.Null(ClientTrustLayers.MachineOnly(_layers.Machine).ForgetUser(Host));
    }
}
