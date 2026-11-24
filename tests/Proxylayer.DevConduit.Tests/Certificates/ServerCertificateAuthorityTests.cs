using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The server's certificate store and authority on a throwaway data root: converging and
/// rotating, the client authority on a host name change, how the files sit on disk, and the
/// store's lock; enrollments are in <see cref="ClientEnrollmentLifecycleTests"/>. No Windows
/// certificate store is touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerCertificateAuthorityTests : IDisposable
{
    private const string Host = "devconduit.example.internal";

    private readonly TempDirectory _dataRoot = new();
    private readonly ManualClock _clock = new(TestCertificates.Now);
    private readonly ServerCertificateStore _store;
    private readonly ServerCertificateAuthority _authority;
    private readonly List<IDisposable> _disposables = [];

    public ServerCertificateAuthorityTests()
    {
        _store = ServerCertificateStore.ForDataRoot(_dataRoot.Path);
        _authority = new ServerCertificateAuthority(_store, _clock, NullLogger.Instance);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _dataRoot.Dispose();
    }

    private ServerCertificateSet Converge(string hostname = Host)
    {
        var set = _authority.Converge(hostname);
        _disposables.Add(set.Current);
        _disposables.Add(set.ClientAuthority);
        if (set.Next is not null)
        {
            _disposables.Add(set.Next);
        }

        return set;
    }

    private IssuedClientCertificate Issue()
    {
        var issued = _authority.IssueClientCertificate(Host);
        _disposables.Add(issued.Certificate);
        return issued;
    }

    private string[] StoredFiles() =>
        [.. Directory.GetFiles(_store.Directory, "*.pfx.dpapi").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    // ---- Converge ------------------------------------------------------------------------

    [Fact]
    public void Converge_OnAnEmptyDataRoot_CreatesTheServerCertificateAndTheAuthority()
    {
        var set = Converge();
        var index = _store.ReadIndex();

        Assert.Equal(Path.Combine(_dataRoot.Path, "certs"), _store.Directory);
        Assert.Equal(Host, set.Hostname);
        Assert.Null(set.Next);
        Assert.True(set.Current.HasPrivateKey);
        Assert.True(set.ClientAuthority.HasPrivateKey);
        Assert.Null(ServerCertificateRules.Problem(set.Current, Host));
        Assert.True(CertificateFactory.IsAuthority(set.ClientAuthority));

        Assert.Equal(set.Current.Thumbprint, index.Current!.Thumbprint);
        Assert.Equal(set.ClientAuthority.Thumbprint, index.ClientAuthority!.Thumbprint);
        Assert.Null(index.Next);
        Assert.Equal(Host, index.Current.Hostname);
        Assert.Equal(new DateTimeOffset(set.Current.NotAfter.ToUniversalTime()), index.Current.NotAfter);
        Assert.Equal(
            [$"client-ca-{set.ClientAuthority.Thumbprint}.pfx.dpapi", $"server-{set.Current.Thumbprint}.pfx.dpapi"],
            StoredFiles());
        Assert.Equal([set.Current.Thumbprint], set.Published.Select(certificate => certificate.Thumbprint));
    }

    [Fact]
    public void Converge_NormalizesTheHostname()
    {
        var set = Converge(" DevConduit.Example.Internal. ");

        Assert.Equal(Host, set.Hostname);
        Assert.Equal(Host, _store.ReadIndex().Current!.Hostname);
    }

    [Fact]
    public void Converge_ASecondTime_IsANoOp()
    {
        var first = Converge();
        var indexText = File.ReadAllText(_store.IndexPath);
        var indexWritten = File.GetLastWriteTimeUtc(_store.IndexPath);
        var files = StoredFiles();

        _clock.Advance(TimeSpan.FromDays(10));
        var second = Converge();

        Assert.Equal(first.Current.Thumbprint, second.Current.Thumbprint);
        Assert.Equal(first.ClientAuthority.Thumbprint, second.ClientAuthority.Thumbprint);
        Assert.Equal(indexText, File.ReadAllText(_store.IndexPath));
        Assert.Equal(indexWritten, File.GetLastWriteTimeUtc(_store.IndexPath));
        Assert.Equal(files, StoredFiles());
    }

    [Fact]
    public void Converge_RestrictsTheCertificateFolder()
    {
        Converge();

        var security = new DirectoryInfo(_store.Directory).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(KeyDirectoryAccess.IsRestricted(security));
    }

    [Fact]
    public void Converge_InsideSixtyDays_PublishesTheNextCertificate()
    {
        var first = Converge();
        _clock.Now = new DateTimeOffset(first.Current.NotAfter.ToUniversalTime()) - TimeSpan.FromDays(45);

        var second = Converge();

        Assert.Equal(first.Current.Thumbprint, second.Current.Thumbprint);
        Assert.NotNull(second.Next);
        Assert.Equal([first.Current.Thumbprint, second.Next.Thumbprint], second.Published.Select(certificate => certificate.Thumbprint));
        Assert.Equal(second.Next.Thumbprint, _store.ReadIndex().Next!.Thumbprint);
        Assert.Equal(3, StoredFiles().Length);
    }

    [Fact]
    public void Converge_InsideThirtyDays_SwitchesToTheNextAndDeletesTheOld()
    {
        var first = Converge();
        var expiry = new DateTimeOffset(first.Current.NotAfter.ToUniversalTime());
        _clock.Now = expiry - TimeSpan.FromDays(45);
        var next = Converge().Next!;

        _clock.Now = expiry - TimeSpan.FromDays(20);
        var switched = Converge();

        Assert.Equal(next.Thumbprint, switched.Current.Thumbprint);
        Assert.Null(switched.Next);
        Assert.DoesNotContain($"server-{first.Current.Thumbprint}.pfx.dpapi", StoredFiles());
        Assert.Equal(first.ClientAuthority.Thumbprint, switched.ClientAuthority.Thumbprint);
    }

    [Fact]
    public void Converge_LongAfterExpiry_ReplacesTheCurrentCertificate()
    {
        var first = Converge();
        _clock.Now = new DateTimeOffset(first.Current.NotAfter.ToUniversalTime()).AddDays(10);

        var replaced = Converge();

        Assert.NotEqual(first.Current.Thumbprint, replaced.Current.Thumbprint);
        Assert.Null(replaced.Next);
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public void Converge_ForANewHostname_ReplacesTheServerCertificateAndTheAuthority()
    {
        var first = Converge();

        var renamed = Converge("new-host.devconduit.internal");

        Assert.NotEqual(first.Current.Thumbprint, renamed.Current.Thumbprint);
        Assert.Null(ServerCertificateRules.Problem(renamed.Current, "new-host.devconduit.internal"));
        Assert.NotEqual(first.ClientAuthority.Thumbprint, renamed.ClientAuthority.Thumbprint);
        Assert.Equal("CN=new-host.devconduit.internal client CA", renamed.ClientAuthority.Subject);
        Assert.Equal("new-host.devconduit.internal", _store.ReadIndex().ClientAuthority!.Hostname);
        Assert.DoesNotContain($"client-ca-{first.ClientAuthority.Thumbprint}.pfx.dpapi", StoredFiles());
    }

    [Fact]
    public void Converge_ForTheSameHostnameInAnotherCase_KeepsTheAuthorityAndItsEnrollments()
    {
        var first = Converge();
        var issued = Issue();

        var again = Converge(Host.ToUpperInvariant());

        Assert.Equal(first.ClientAuthority.Thumbprint, again.ClientAuthority.Thumbprint);
        Assert.True(_authority.ReadIssued().IsAccepted(issued.Record.Serial));
    }

    [Fact]
    public void Converge_AfterTheAuthorityExpires_CreatesANewOneDeletesTheOldAndRevokesItsEnrollments()
    {
        var first = Converge();
        var issued = Issue();
        _clock.Now = new DateTimeOffset(first.ClientAuthority.NotAfter.ToUniversalTime()).AddDays(1);

        var later = Converge();

        Assert.NotEqual(first.ClientAuthority.Thumbprint, later.ClientAuthority.Thumbprint);
        Assert.DoesNotContain($"client-ca-{first.ClientAuthority.Thumbprint}.pfx.dpapi", StoredFiles());
        Assert.Equal(
            ServerCertificateAuthority.AuthorityExpiredRevocation,
            _authority.ReadIssued().FindEnrollment(issued.Record.Enrollment)!.RevokedReason);
    }

    // ---- On disk -------------------------------------------------------------------------

    [Fact]
    public void StoredFiles_AreDpapiProtectedNotPlainPkcs12()
    {
        var set = Converge();
        var path = Path.Combine(_store.Directory, $"server-{set.Current.Thumbprint}.pfx.dpapi");
        var onDisk = File.ReadAllBytes(path);

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(onDisk, password: null, X509KeyStorageFlags.EphemeralKeySet));

        using var decrypted = X509CertificateLoader.LoadPkcs12(DpapiProtectedFile.Read(path), password: null, X509KeyStorageFlags.EphemeralKeySet);
        Assert.Equal(set.Current.Thumbprint, decrypted.Thumbprint);
        Assert.True(decrypted.HasPrivateKey);
    }

    [Fact]
    public void StoredFiles_DoNotDecryptWithOtherEntropy()
    {
        var set = Converge();
        var onDisk = File.ReadAllBytes(Path.Combine(_store.Directory, $"server-{set.Current.Thumbprint}.pfx.dpapi"));

        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(onDisk, null, DataProtectionScope.LocalMachine));
    }

    [Fact]
    public void LoadPkcs12_RefusesAnIndexEntryWithAPath()
    {
        Converge();
        var stored = new StoredCertificate(@"..\escape.pfx.dpapi", "T", Host, TestCertificates.Now, TestCertificates.Now.AddDays(1));

        Assert.Throws<InvalidDataException>(() => _store.LoadPkcs12(stored));
        Assert.Throws<InvalidDataException>(() => _store.Delete(stored));
    }

    [Fact]
    public void Store_RefusesARelativeDirectory() =>
        Assert.Throws<ArgumentException>(() => new ServerCertificateStore(@"relative\certs"));

    [Fact]
    public void ReadIndex_OfAnEmptyStore_IsEmpty()
    {
        Assert.Equal(ServerCertificateIndex.Empty, _store.ReadIndex());
        Assert.Empty(_store.ReadIssued().Records);
    }

    // ---- Lock ----------------------------------------------------------------------------

    [Fact]
    public void Lock_CanBeTakenAgainOnceReleased()
    {
        _store.Prepare();

        using (_store.Lock())
        {
        }

        using (_store.Lock())
        {
        }

        Assert.True(File.Exists(Path.Combine(_store.Directory, ".lock")));
    }

    [Fact]
    public async Task Lock_WaitsForTheHolderAndThenSucceeds()
    {
        _store.Prepare();
        var held = _store.Lock();
        var released = false;

        var waiter = Task.Run(() =>
        {
            using (_store.Lock())
            {
                return Volatile.Read(ref released);
            }
        });

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);
        Volatile.Write(ref released, true);
        held.Dispose();

        Assert.True(await waiter.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Converge_WhileTheStoreIsHeldBySomeoneElse_WaitsForIt()
    {
        _store.Prepare();
        var held = _store.Lock();
        using var release = new Timer(_ => held.Dispose(), null, 300, Timeout.Infinite);

        var set = Converge();

        Assert.Equal(Host, set.Hostname);
    }
}
