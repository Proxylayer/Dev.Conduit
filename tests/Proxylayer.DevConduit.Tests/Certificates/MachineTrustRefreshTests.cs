using System.Net;
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
/// What <c>service-admin trust</c> does, without elevation: the server's <c>/cert</c> answer is
/// stood in for by a delegate, the machine layer is a throwaway folder, and the current user's
/// AddressBook store stands in for <c>LocalMachine\Root</c>; whatever lands in it is removed
/// afterwards. A user layer with a planted certificate sits beside the machine layer to show
/// it is never read. <c>/cert/renew</c> is stood in for by a second delegate that signs with
/// the test's client authority, as the server would.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineTrustRefreshTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const StoreName ScratchStore = StoreName.AddressBook;
    private const StoreLocation ScratchLocation = StoreLocation.CurrentUser;

    private readonly TempDirectory _root = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly ProxyConfigImportTargets _targets;
    private readonly ClientTrustLayers _layers;
    private readonly X509Certificate2 _authority;
    private readonly X509Certificate2 _identity;
    private readonly X509Certificate2 _current;
    private readonly X509Certificate2 _next;
    private readonly X509Certificate2 _planted;
    private readonly HashSet<string> _thumbprints = new(StringComparer.OrdinalIgnoreCase);

    public MachineTrustRefreshTests()
    {
        var now = _clock.Now;
        _authority = CertificateFactory.CreateClientAuthority(Host, now.AddDays(-30));
        _identity = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, now, TestCertificates.Enrollment), now);
        _current = CertificateFactory.CreateServerCertificate(Host, now.AddDays(-300));
        _next = CertificateFactory.CreateServerCertificate(Host, now.AddDays(-1));
        _planted = CertificateFactory.CreateServerCertificate(Host, now.AddDays(-1));
        _thumbprints.UnionWith([_current.Thumbprint, _next.Thumbprint, _planted.Thumbprint]);

        var settingsPath = Path.Combine(_root.Path, "machine", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, $$"""{ "DevConduit": { "Remotes": { "devconduit": { "Endpoint": "https://{{Host}}:9443" } } } }""");

        _targets = new ProxyConfigImportTargets(
            new HostsFile(Path.Combine(_root.Path, "etc", "hosts")),
            new ClientCertificateDirectory(Path.Combine(_root.Path, "machine", "client"), restrictFolders: false),
            settingsPath,
            ScratchStore,
            ScratchLocation,
            ProtectMachineSettings: false);
        _layers = new ClientTrustLayers(_targets.ClientDirectory, Path.Combine(_root.Path, "user", "client"));

        // What an import left: the current certificate pinned, and the machine identity.
        var machine = _layers.MachineFolder(Host);
        machine.WriteTrust([PinnedTrust.Pin(_current)]);
        machine.WriteIdentity(CertificateFactory.ExportPkcs12(_identity));

        // What a user's sync, or someone writing as that user, left in the overlay.
        _layers.UserFolder(Host)!.WriteTrust([PinnedTrust.Pin(_planted)]);
    }

    public void Dispose()
    {
        using (var store = new X509Store(ScratchStore, ScratchLocation))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var thumbprint in _thumbprints)
            {
                foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
                {
                    store.Remove(certificate);
                    certificate.Dispose();
                }
            }
        }

        _identity.Dispose();
        _authority.Dispose();
        _current.Dispose();
        _next.Dispose();
        _planted.Dispose();
        _root.Dispose();
    }

    private sealed record Call(Uri BaseAddress, string IdentityThumbprint, RemoteCertificatePolicy Policy);

    /// <summary>A server that publishes <paramref name="published"/> and records how it was called.</summary>
    private static MachineTrustRefresh.CertFetch Server(List<Call> calls, string hostname, params X509Certificate2[] published) =>
        (baseAddress, identity, policy, _) =>
        {
            calls.Add(new Call(baseAddress, identity.Thumbprint, policy));
            return Task.FromResult(new CertResponse(
                hostname,
                [.. published.Select(PinnedTrust.Pin)],
                new ClientCertificateStatus(identity.NotAfter, identity.SerialNumber, identity.NotAfter.AddDays(-60))));
        };

    private Task<MachineTrustRefreshResult> Refresh(MachineTrustRefresh.CertFetch fetch, string hostname = Host) =>
        new MachineTrustRefresh(_targets, _clock, fetch).RefreshAsync(hostname, CancellationToken.None);

    private Task<MachineTrustRefreshResult> Refresh(MachineTrustRefresh.CertFetch fetch, MachineTrustRefresh.CertRenew renew) =>
        new MachineTrustRefresh(_targets, _clock, fetch, renew).RefreshAsync(Host, CancellationToken.None);

    private sealed record RenewCall(Uri BaseAddress, string IdentityThumbprint, byte[] SigningRequest);

    /// <summary>A server that renews as the real one does: the request's subject and key, signed by the client authority.</summary>
    private MachineTrustRefresh.CertRenew Renewing(List<RenewCall> calls) =>
        (baseAddress, identity, _, signingRequest, _) =>
        {
            calls.Add(new RenewCall(baseAddress, identity.Thumbprint, signingRequest));
            using var issued = CertificateFactory.IssueClientCertificate(
                _authority, CertificateRequest.LoadSigningRequest(signingRequest, HashAlgorithmName.SHA256), _clock.Now);
            return Task.FromResult(Answer(issued));
        };

    /// <summary>A server that answers <c>/cert/renew</c> with <paramref name="issue"/>'s certificate instead of one for the request.</summary>
    private MachineTrustRefresh.CertRenew Answering(Func<X509Certificate2> issue) =>
        (_, _, _, _, _) =>
        {
            using var issued = issue();
            return Task.FromResult(Answer(issued));
        };

    private static MachineTrustRefresh.CertRenew Refusing(HttpStatusCode status) =>
        (_, _, _, _, _) => throw new HttpRequestException($"The server answered {(int)status}.", null, status);

    private static CertRenewResponse Answer(X509Certificate2 issued) =>
        new(Convert.ToBase64String(issued.RawData), issued.SerialNumber, new DateTimeOffset(issued.NotAfter.ToUniversalTime()));

    /// <summary>Moves the clock to thirty days before the machine identity expires, inside its renewal window.</summary>
    private void MachineIdentityDue() => _clock.Now = new DateTimeOffset(_identity.NotAfter.ToUniversalTime()).AddDays(-30);

    private byte[] MachineIdentityFile() => File.ReadAllBytes(_layers.MachineFolder(Host).IdentityPath);

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

    private static int InScratchStore(X509Certificate2 certificate)
    {
        using var store = new X509Store(ScratchStore, ScratchLocation);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false);
        var count = found.Count;
        foreach (var match in found)
        {
            match.Dispose();
        }

        return count;
    }

    private IReadOnlyList<string> MachinePins() =>
        _layers.MachineFolder(Host).ReadTrust().Select(pin => pin.Thumbprint).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Refresh_TakesTheCertificatesFromCertNotFromTheUserLayer()
    {
        var calls = new List<Call>();

        var result = await Refresh(Server(calls, Host, _current, _next));

        Assert.True(result.Found);
        Assert.Equal(new[] { _current.Thumbprint, _next.Thumbprint }.Order(StringComparer.Ordinal), MachinePins());
        Assert.Equal(1, InScratchStore(_current));
        Assert.Equal(1, InScratchStore(_next));

        // The planted certificate in the user's overlay reached neither the machine pins nor Windows trust.
        Assert.Equal(0, InScratchStore(_planted));
        Assert.DoesNotContain(_planted.Thumbprint, MachinePins());
        Assert.Equal([_planted.Thumbprint], _layers.UserFolder(Host)!.ReadTrust().Select(pin => pin.Thumbprint));
    }

    [Fact]
    public async Task Refresh_CallsCertWithTheMachineIdentityAndAMachineOnlyPolicy()
    {
        var calls = new List<Call>();

        await Refresh(Server(calls, Host, _current));

        var call = Assert.Single(calls);
        Assert.Equal(new Uri($"https://{Host}:9443/"), call.BaseAddress);
        Assert.Equal(_identity.Thumbprint, call.IdentityThumbprint);

        // The policy accepts the server by the machine pins alone: never by the user's planted pin.
        Assert.True(call.Policy.Accepts(Host, _current, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(call.Policy.Accepts(Host, _planted, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task Refresh_UsesTheMachineIdentityEvenBesideAUserRenewal()
    {
        using var renewal = CertificateFactory.CreateClientCertificate(
            _authority, CertificateFactory.ClientSubject(Host, _clock.Now, TestCertificates.Enrollment), _clock.Now);
        _layers.UserFolder(Host)!.WriteIdentity(CertificateFactory.ExportPkcs12(renewal));
        var calls = new List<Call>();

        await Refresh(Server(calls, Host, _current));

        Assert.Equal(_identity.Thumbprint, Assert.Single(calls).IdentityThumbprint);
    }

    [Fact]
    public async Task Refresh_AppliesTheServerCertificateRulesToEveryCertificate()
    {
        using var otherHost = CertificateFactory.CreateServerCertificate("other.devconduit.internal", _clock.Now);
        using var authority = TestCertificates.SelfSigned(Host, [Host], authority: true);
        _thumbprints.UnionWith([otherHost.Thumbprint, authority.Thumbprint]);

        var result = await Refresh(Server([], Host, _current, otherHost, authority));

        Assert.Equal([_current.Thumbprint], MachinePins());
        Assert.Equal(0, InScratchStore(otherHost));
        Assert.Equal(0, InScratchStore(authority));
        Assert.Equal(2, result.Summary.Count(line => line.StartsWith("Not trusted:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Refresh_RemovesTheExpiredCertificatesThisAppAdded()
    {
        await Refresh(Server([], Host, _current, _next));
        Assert.Equal(1, InScratchStore(_current));

        _clock.Now = new DateTimeOffset(_current.NotAfter.ToUniversalTime()).AddDays(1);
        await Refresh(Server([], Host, _next));

        Assert.Equal(0, InScratchStore(_current));
        Assert.Equal(1, InScratchStore(_next));
        Assert.Equal([_next.Thumbprint], MachinePins());
    }

    [Fact]
    public async Task Refresh_OfAServerAnsweringForAnotherHost_ChangesNothing()
    {
        var before = File.ReadAllBytes(_layers.MachineFolder(Host).TrustPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Refresh(Server([], "other.devconduit.internal", _next)));

        Assert.Equal(before, File.ReadAllBytes(_layers.MachineFolder(Host).TrustPath));
        Assert.Equal(0, InScratchStore(_next));
    }

    [Fact]
    public async Task Refresh_WhenTheServerRefuses_ChangesNothing()
    {
        var before = File.ReadAllBytes(_layers.MachineFolder(Host).TrustPath);
        MachineTrustRefresh.CertFetch refusing = (_, _, _, _) =>
            throw new HttpRequestException("The server answered 403.", null, System.Net.HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<HttpRequestException>(() => Refresh(refusing));

        Assert.Equal(before, File.ReadAllBytes(_layers.MachineFolder(Host).TrustPath));
        Assert.Equal(0, InScratchStore(_current));
    }

    [Fact]
    public async Task Refresh_OfAServerWithoutAMachineIdentity_IsNotFoundAndCallsNothing()
    {
        var calls = new List<Call>();
        File.Delete(_layers.MachineFolder(Host).IdentityPath);

        var result = await Refresh(Server(calls, Host, _next));

        Assert.False(result.Found);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Refresh_OfAServerWithoutARemote_IsNotFound()
    {
        File.WriteAllText(_targets.MachineSettingsPath, "{ }");
        var calls = new List<Call>();

        var result = await Refresh(Server(calls, Host, _next));

        Assert.False(result.Found);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Refresh_TakesOnlyThePortFromTheSettings()
    {
        File.WriteAllText(
            _targets.MachineSettingsPath,
            $$"""{ "DevConduit": { "Remotes": { "elsewhere": "https://attacker.example:1", "devconduit": "https://{{Host.ToUpperInvariant()}}:7443/path" } } }""");
        var calls = new List<Call>();

        await Refresh(Server(calls, Host, _current));

        Assert.Equal(new Uri($"https://{Host}:7443/"), Assert.Single(calls).BaseAddress);
    }

    [Fact]
    public async Task Refresh_RefusesANameThatIsNotAHost() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Refresh(Server([], Host), "10.0.0.5"));

    // ---- Machine identity renewal ------------------------------------------------------------

    [Fact]
    public async Task Refresh_WhenTheMachineIdentityIsDue_RenewsItInTheMachineLayer()
    {
        MachineIdentityDue();
        var renewals = new List<RenewCall>();

        var result = await Refresh(Server([], Host, _next), Renewing(renewals));

        Assert.True(result.Found);
        Assert.Equal(MachineIdentityRenewal.Renewed, result.Identity);
        Assert.Contains(result.Summary, line => line.StartsWith("Machine client certificate renewed as ", StringComparison.Ordinal));

        using var renewed = _layers.MachineFolder(Host).ReadIdentity();
        Assert.NotNull(renewed);
        Assert.True(renewed.HasPrivateKey);
        Assert.NotEqual(_identity.SerialNumber, renewed.SerialNumber);
        Assert.Equal(_identity.SubjectName.RawData, renewed.SubjectName.RawData);
        Assert.True(renewed.NotAfter > _identity.NotAfter);

        // The pins and Windows trust were brought up to date as well.
        Assert.Contains(_next.Thumbprint, MachinePins());
        Assert.Equal(1, InScratchStore(_next));
    }

    [Fact]
    public async Task Refresh_RenewalAsksForTheSameSubjectWithANewKey()
    {
        MachineIdentityDue();
        var renewals = new List<RenewCall>();

        await Refresh(Server([], Host, _next), Renewing(renewals));

        var call = Assert.Single(renewals);
        var request = CertificateRequest.LoadSigningRequest(call.SigningRequest, HashAlgorithmName.SHA256);
        Assert.Equal(_identity.SubjectName.RawData, request.SubjectName.RawData);
        Assert.NotEqual(_identity.PublicKey.EncodedKeyValue.RawData, request.PublicKey.EncodedKeyValue.RawData);
    }

    [Fact]
    public async Task Refresh_RenewsWithTheMachineIdentityAndNeverTouchesTheUserLayer()
    {
        MachineIdentityDue();
        using var userRenewal = CertificateFactory.CreateClientCertificate(
            _authority, CertificateFactory.ClientSubject(Host, _clock.Now, TestCertificates.Enrollment), _clock.Now);
        var userFolder = _layers.UserFolder(Host)!;
        userFolder.WriteIdentity(CertificateFactory.ExportPkcs12(userRenewal));
        var userFile = File.ReadAllBytes(userFolder.IdentityPath);
        var calls = new List<Call>();
        var renewals = new List<RenewCall>();

        await Refresh(Server(calls, Host, _next), Renewing(renewals));

        Assert.Equal(_identity.Thumbprint, Assert.Single(calls).IdentityThumbprint);
        var renewal = Assert.Single(renewals);
        Assert.Equal(_identity.Thumbprint, renewal.IdentityThumbprint);
        Assert.Equal(new Uri($"https://{Host}:9443/"), renewal.BaseAddress);
        Assert.Equal(userFile, File.ReadAllBytes(userFolder.IdentityPath));
    }

    [Fact]
    public async Task Refresh_WhenTheMachineIdentityIsNotDue_LeavesItAlone()
    {
        var before = MachineIdentityFile();
        var renewals = new List<RenewCall>();

        var result = await Refresh(Server([], Host, _current, _next), Renewing(renewals));

        Assert.Equal(MachineIdentityRenewal.NotDue, result.Identity);
        Assert.Empty(renewals);
        Assert.Equal(before, MachineIdentityFile());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Refresh_WhenTheRenewalIsRefused_KeepsTheIdentityAndTheTrustUpdate(HttpStatusCode status)
    {
        MachineIdentityDue();
        var before = MachineIdentityFile();

        var result = await Refresh(Server([], Host, _next), Refusing(status));

        Assert.True(result.Found);
        Assert.Equal(MachineIdentityRenewal.Failed, result.Identity);
        Assert.Equal(before, MachineIdentityFile());
        var line = Assert.Single(result.Summary, line => line.StartsWith("Machine client certificate not renewed:", StringComparison.Ordinal));
        Assert.Contains($"{(int)status}", line, StringComparison.Ordinal);
        Assert.Contains("is kept", line, StringComparison.Ordinal);

        // The pins and Windows trust stand.
        Assert.Contains(_next.Thumbprint, MachinePins());
        Assert.Equal(1, InScratchStore(_next));
    }

    [Fact]
    public async Task Refresh_WhenTheRenewalCannotBeReached_KeepsTheIdentity()
    {
        MachineIdentityDue();
        var before = MachineIdentityFile();
        MachineTrustRefresh.CertRenew unreachable = (_, _, _, _, _) => throw new TaskCanceledException("The request timed out.");

        var result = await Refresh(Server([], Host, _next), unreachable);

        Assert.Equal(MachineIdentityRenewal.Failed, result.Identity);
        Assert.Equal(before, MachineIdentityFile());
    }

    [Fact]
    public async Task Refresh_WhenTheRenewedCertificateNamesAnotherSubject_KeepsTheIdentity()
    {
        MachineIdentityDue();
        var before = MachineIdentityFile();
        const string Other = "fedcba9876543210fedcba9876543210";

        var result = await Refresh(
            Server([], Host, _next),
            Answering(() => CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, _clock.Now, Other), _clock.Now)));

        Assert.Equal(MachineIdentityRenewal.Failed, result.Identity);
        Assert.Equal(before, MachineIdentityFile());
    }

    [Fact]
    public async Task Refresh_WhenTheRenewedCertificateIsForAnotherKey_KeepsTheIdentity()
    {
        MachineIdentityDue();
        var before = MachineIdentityFile();

        var result = await Refresh(
            Server([], Host, _next),
            Answering(() => CertificateFactory.CreateClientCertificate(_authority, _identity.SubjectName, _clock.Now)));

        Assert.Equal(MachineIdentityRenewal.Failed, result.Identity);
        Assert.Equal(before, MachineIdentityFile());
    }

    [Fact]
    public async Task Refresh_WhenTheRenewalIsCancelled_Throws()
    {
        MachineIdentityDue();
        using var cancellation = new CancellationTokenSource();
        MachineTrustRefresh.CertRenew cancelling = (_, _, _, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Not reached.");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MachineTrustRefresh(_targets, _clock, Server([], Host, _next), cancelling).RefreshAsync(Host, cancellation.Token));
    }

    // ---- The machine folder's access list ----------------------------------------------------

    [Fact]
    public async Task Refresh_TightensAMachineFolderAnEarlierBuildLeftBroad()
    {
        // The real layout, restricted folders and all, over the same throwaway root.
        var targets = _targets with { ClientDirectory = new ClientCertificateDirectory(_targets.ClientDirectory.Root) };
        var directory = new DirectoryInfo(targets.ClientDirectory.Folder(Host).Directory);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var broad = directory.GetAccessControl(AccessControlSections.Access);
        broad.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(broad);
        Assert.False(ClientTrustFolder.IsRestricted(directory.GetAccessControl(AccessControlSections.Access)));

        try
        {
            // The server publishes what is pinned already and the identity is not due, so nothing in the folder is written.
            var first = await new MachineTrustRefresh(targets, _clock, Server([], Host, _current)).RefreshAsync(Host, CancellationToken.None);

            Assert.True(first.Found);
            Assert.Contains(first.Summary, line => line.StartsWith($"Limited {directory.FullName} ", StringComparison.Ordinal));
            var tightened = directory.GetAccessControl(AccessControlSections.Access);
            Assert.True(tightened.AreAccessRulesProtected);
            Assert.True(ClientTrustFolder.IsRestricted(tightened));
            Assert.DoesNotContain(
                tightened.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => rule.IdentityReference.Equals(users) && (rule.FileSystemRights & FileSystemRights.WriteData) != 0);

            // Once restricted, it is left as it is.
            var second = await new MachineTrustRefresh(targets, _clock, Server([], Host, _current)).RefreshAsync(Host, CancellationToken.None);
            Assert.DoesNotContain(second.Summary, line => line.StartsWith("Limited ", StringComparison.Ordinal));
        }
        finally
        {
            Release(directory.FullName);
        }
    }

    [Fact]
    public async Task Refresh_LeavesAnUnrestrictedTestFolderAsItIs()
    {
        var before = _layers.MachineFolder(Host).Directory;

        var result = await Refresh(Server([], Host, _current));

        Assert.DoesNotContain(result.Summary, line => line.StartsWith("Limited ", StringComparison.Ordinal));
        Assert.False(new DirectoryInfo(before).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
    }
}
