using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// How the relay validates a remote's TLS certificate: Windows' verdict when it has no
/// complaint, otherwise only a pinned, valid certificate naming the host. Pins live in
/// <c>trust.json</c> under a throwaway client root; two tests run a real TLS handshake over
/// loopback so the host comes from the <see cref="SslStream"/> as it does in the relay.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteCertificatePolicyTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string OtherHost = "other.devconduit.internal";
    private const SslPolicyErrors Untrusted = SslPolicyErrors.RemoteCertificateChainErrors;

    private readonly TempDirectory _root = new();
    private readonly ManualClock _clock = new(TestCertificates.Now);
    private readonly ClientCertificateDirectory _directory;
    private readonly RemoteCertificatePolicy _policy;
    private readonly X509Certificate2 _server = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now.AddDays(-1));

    public RemoteCertificatePolicyTests()
    {
        _directory = new ClientCertificateDirectory(Path.Combine(_root.Path, "client"));
        _policy = new RemoteCertificatePolicy(new ClientTrustStore(ClientTrustLayers.MachineOnly(_directory)), _clock);
    }

    public void Dispose()
    {
        _server.Dispose();
        _root.Dispose();
    }

    private void PinFor(string hostname, params X509Certificate2[] certificates) =>
        _directory.Folder(hostname).WriteTrust([.. certificates.Select(PinnedTrust.Pin)]);

    // ---- No errors -----------------------------------------------------------------------

    [Fact]
    public void NoErrors_IsAcceptedWithoutLookingAtPins()
    {
        using var stranger = CertificateFactory.CreateServerCertificate(OtherHost, TestCertificates.Now);

        Assert.True(_policy.Accepts(Host, stranger, SslPolicyErrors.None));
        Assert.True(_policy.Accepts(null, null, SslPolicyErrors.None));
        Assert.True(_policy.Validate(new object(), null, null, SslPolicyErrors.None));
    }

    // ---- Pinned --------------------------------------------------------------------------

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void APinnedValidLeafNamingTheHost_IsAccepted(SslPolicyErrors errors)
    {
        PinFor(Host, _server);

        Assert.True(_policy.Accepts(Host, _server, errors));
        Assert.True(_policy.Accepts(Host.ToUpperInvariant(), _server, errors));
    }

    [Fact]
    public void APinnedLeafAsAPlainX509Certificate_IsAccepted()
    {
        PinFor(Host, _server);
#pragma warning disable SYSLIB0057 // The callback may hand over the base type; this is that case.
        using var plain = new X509Certificate(_server.RawData);
#pragma warning restore SYSLIB0057

        Assert.True(_policy.Accepts(Host, plain, Untrusted));
    }

    [Fact]
    public void APinnedLeafPastItsExpiry_IsRefused()
    {
        PinFor(Host, _server);
        _clock.Now = new DateTimeOffset(_server.NotAfter.ToUniversalTime());

        Assert.False(_policy.Accepts(Host, _server, Untrusted));

        _clock.Now = _clock.Now.AddSeconds(-1);
        Assert.True(_policy.Accepts(Host, _server, Untrusted));
    }

    [Fact]
    public void APinnedLeafNotYetValid_IsRefused()
    {
        PinFor(Host, _server);
        _clock.Now = new DateTimeOffset(_server.NotBefore.ToUniversalTime()).AddSeconds(-1);

        Assert.False(_policy.Accepts(Host, _server, Untrusted));
    }

    [Fact]
    public void ALeafPinnedForAnotherHost_IsRefused()
    {
        // The certificate names Host, but is offered by OtherHost.
        PinFor(Host, _server);

        Assert.False(_policy.Accepts(OtherHost, _server, Untrusted));
    }

    [Fact]
    public void ALeafPinnedUnderAHostItDoesNotName_IsRefused()
    {
        // A trust.json for OtherHost that pins Host's certificate: the name check still refuses it.
        PinFor(OtherHost, _server);

        Assert.False(_policy.Accepts(OtherHost, _server, Untrusted));
    }

    [Fact]
    public void ALeafNotPinned_IsRefused()
    {
        using var unpinned = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now.AddDays(-1));
        PinFor(Host, _server);

        Assert.False(_policy.Accepts(Host, unpinned, Untrusted));
    }

    [Fact]
    public void AHostNothingWasImportedFor_IsRefusedOnAnyError()
    {
        Assert.False(_policy.Accepts(Host, _server, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(_policy.Accepts(Host, _server, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void AMissingCertificate_IsRefusedEvenWhenPinsExist()
    {
        PinFor(Host, _server);

        Assert.False(_policy.Accepts(Host, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(_policy.Accepts(Host, _server, SslPolicyErrors.RemoteCertificateNotAvailable | Untrusted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.0.5")]
    [InlineData("not a host")]
    public void NoUsableHost_IsRefused(string? hostname)
    {
        PinFor(Host, _server);

        Assert.False(_policy.Accepts(hostname, _server, Untrusted));
    }

    [Fact]
    public void Validate_WithASenderThatIsNotAnSslStream_HasNoHostAndRefusesErrors()
    {
        PinFor(Host, _server);
        using var chain = new X509Chain();

        Assert.False(_policy.Validate(new object(), _server, chain, Untrusted));
    }

    [Fact]
    public void ADamagedTrustFile_RefusesWithoutThrowing()
    {
        var folder = _directory.Folder(Host);
        Directory.CreateDirectory(folder.Directory);
        File.WriteAllText(folder.TrustPath, "{ not json");

        Assert.False(_policy.Accepts(Host, _server, Untrusted));
    }

    [Fact]
    public void ATrustFileForAnotherHostName_IsIgnored()
    {
        // trust.json names the host it pins for; a copied file is not honoured.
        PinFor(OtherHost, _server);
        File.Copy(_directory.Folder(OtherHost).TrustPath, Path.Combine(EnsureFolder(Host), ClientTrustFolder.TrustFileName));

        Assert.False(_policy.Accepts(Host, _server, Untrusted));
    }

    [Fact]
    public void ANewPin_IsPickedUpAfterTheFileChanges()
    {
        PinFor(Host, _server);
        using var next = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);
        Assert.False(_policy.Accepts(Host, next, Untrusted));

        PinFor(Host, _server, next);
        File.SetLastWriteTimeUtc(_directory.Folder(Host).TrustPath, DateTime.UtcNow.AddSeconds(5));

        Assert.True(_policy.Accepts(Host, next, Untrusted));
    }

    [Fact]
    public void IsPinned_IsTheRuleOnItsOwn()
    {
        using var pin = TestCertificates.PublicOnly(_server);
        var now = TestCertificates.Now;

        Assert.True(RemoteCertificatePolicy.IsPinned(Host, _server, [pin], now));
        Assert.False(RemoteCertificatePolicy.IsPinned(Host, _server, [], now));
        Assert.False(RemoteCertificatePolicy.IsPinned(OtherHost, _server, [pin], now));
        Assert.False(RemoteCertificatePolicy.IsPinned(Host, _server, [pin], new DateTimeOffset(_server.NotAfter.ToUniversalTime())));
    }

    private string EnsureFolder(string hostname)
    {
        var directory = _directory.Folder(hostname).Directory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ---- Through a real handshake over loopback ------------------------------------------

    [Fact]
    public async Task Handshake_ToAPinnedServer_Succeeds()
    {
        PinFor(Host, _server);
        _clock.Now = DateTimeOffset.UtcNow;

        var negotiated = await HandshakeAsync(_server);

        Assert.True(negotiated);
    }

    [Fact]
    public async Task Handshake_ToAnUnpinnedServer_IsRefused()
    {
        using var unpinned = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now.AddDays(-1));
        PinFor(Host, _server);
        _clock.Now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<AuthenticationException>(() => HandshakeAsync(unpinned));
    }

    /// <summary>A TLS handshake to <see cref="Host"/> over a loopback socket, validated by the policy.</summary>
    private async Task<bool> HandshakeAsync(X509Certificate2 serverCertificate)
    {
        Assert.SkipWhen(
            DateTimeOffset.UtcNow >= new DateTimeOffset(serverCertificate.NotAfter.ToUniversalTime()),
            "The test certificate is dated for a fixed clock that has passed.");

        // Windows TLS needs a key it can use, which an ephemeral CertificateRequest key is not.
        using var usable = CertificateFactory.LoadPkcs12(CertificateFactory.ExportPkcs12(serverCertificate));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(cancel.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, port, cancel.Token);
        using var accepted = await accept;

        await using var serverStream = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
        await using var clientStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

        var serverSide = serverStream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = usable },
            cancel.Token);

        AuthenticationException? refused = null;
        try
        {
            await clientStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = Host,
                    RemoteCertificateValidationCallback = _policy.Validate
                },
                cancel.Token);
        }
        catch (AuthenticationException exception)
        {
            refused = exception;

            // Closing the client ends the server's wait for the rest of the handshake.
            clientStream.Dispose();
            client.Dispose();
        }

        // The server side's own outcome is not what is under test.
        try
        {
            await serverSide.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
        }

        if (refused is not null)
        {
            throw refused;
        }

        return clientStream.IsAuthenticated;
    }
}
