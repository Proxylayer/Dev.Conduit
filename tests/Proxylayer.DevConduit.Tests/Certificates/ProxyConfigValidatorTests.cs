using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The strict check every <c>.conduit</c> passes before anything on the machine changes,
/// with the certificates made by <see cref="CertificateFactory"/>. Pure: nothing is written.
/// </summary>
public sealed class ProxyConfigValidatorTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    private readonly X509Certificate2 _server = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-1));
    private readonly X509Certificate2 _authority = CertificateFactory.CreateClientAuthority(Host, Now.AddDays(-1));
    private readonly X509Certificate2 _client;

    public ProxyConfigValidatorTests()
    {
        _client = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now.AddDays(-1));
    }

    public void Dispose()
    {
        _server.Dispose();
        _authority.Dispose();
        _client.Dispose();
    }

    private ProxyConfigDocument Document() =>
        new(
            ProxyConfigDocument.CurrentFormat,
            Host,
            "10.0.0.5",
            8091,
            "devconduit",
            Tenant,
            Client,
            new Dictionary<string, string> { ["api"] = "https://api.internal:8443", ["web"] = "http://web.internal" },
            [Convert.ToBase64String(_server.RawData)],
            Convert.ToBase64String(CertificateFactory.ExportPkcs12(_client)),
            Now);

    private static string Refusal(ProxyConfigDocument document)
    {
        Assert.False(ProxyConfigValidator.TryValidate(document, Now, out var config, out var error));
        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error;
    }

    private static string Refusal(byte[] utf8)
    {
        Assert.False(ProxyConfigValidator.TryValidate(utf8, Now, out var config, out var error));
        Assert.Null(config);
        return error;
    }

    private static string Base64(X509Certificate2 certificate) => Convert.ToBase64String(certificate.RawData);

    // ---- Happy path ----------------------------------------------------------------------

    [Fact]
    public void AnExportShapedFile_IsAccepted()
    {
        var document = Document();
        Assert.True(ProxyConfigValidator.TryValidate(document, Now, out var config, out var error), error);

        Assert.Equal(string.Empty, error);
        Assert.Equal(Host, config.Hostname);
        Assert.Equal("10.0.0.5", config.Address);
        Assert.True(config.AddressIsIp);
        Assert.Equal(8091, config.Port);
        Assert.Equal("devconduit", config.Remote);
        Assert.Equal(Tenant, config.TenantId);
        Assert.Equal(Client, config.ClientId);
        Assert.Equal($"https://{Host}:8091", config.Endpoint);
        Assert.Equal(_server.Thumbprint, Assert.Single(config.ServerCertificates).Thumbprint);
        Assert.Equal(_client.Thumbprint, config.ClientCertificate.Thumbprint);
        Assert.True(config.ClientCertificate.HasPrivateKey);
        Assert.Equal(Convert.FromBase64String(document.ClientCertificate), config.ClientCertificatePkcs12);
    }

    [Fact]
    public void TheUtf8Form_RoundTripsThroughParse()
    {
        var bytes = Document().ToUtf8();

        Assert.True(ProxyConfigValidator.TryValidate(bytes, Now, out var config, out var error), error);
        Assert.Equal(Host, config.Hostname);
        Assert.Equal(2, config.Routes.Count);
    }

    [Fact]
    public void TheUtf8Form_IsCamelCaseJson()
    {
        var json = JsonNode.Parse(Document().ToUtf8())!.AsObject();

        Assert.Equal(
            ["format", "hostname", "address", "port", "remote", "tenantId", "clientId", "routes", "serverCertificates", "clientCertificate", "exportedAt"],
            json.Select(property => property.Key));
        Assert.Equal("dev-conduit/1", (string?)json["format"]);
    }

    [Fact]
    public void Routes_AreTrimmedToTheirOrigins()
    {
        var document = Document() with
        {
            Routes = new Dictionary<string, string> { [" api "] = " https://API.internal:8443/ ", ["plain"] = "http://web.internal:80" }
        };

        Assert.True(ProxyConfigValidator.TryValidate(document, Now, out var config, out var error), error);

        Assert.Equal(
            [new KeyValuePair<string, string>("api", "https://api.internal:8443"), new KeyValuePair<string, string>("plain", "http://web.internal")],
            config.Routes);
    }

    [Fact]
    public void NoRoutes_IsAccepted()
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { Routes = new Dictionary<string, string>() }, Now, out var config, out var error), error);
        Assert.Empty(config.Routes);
    }

    [Fact]
    public void TheHostname_IsNormalized()
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { Hostname = " DevConduit.Example.Internal. " }, Now, out var config, out var error), error);
        Assert.Equal(Host, config.Hostname);
    }

    [Fact]
    public void AHostnameAddress_IsAcceptedButNotAnIp()
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { Address = " gateway.corp " }, Now, out var config, out var error), error);

        Assert.Equal("gateway.corp", config.Address);
        Assert.False(config.AddressIsIp);
    }

    [Fact]
    public void AnIPv6Address_IsAnIp()
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { Address = "fd00::5" }, Now, out var config, out var error), error);
        Assert.True(config.AddressIsIp);
    }

    [Fact]
    public void ADomainTenant_IsAccepted()
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { TenantId = "contoso.onmicrosoft.com" }, Now, out var config, out var error), error);
        Assert.Equal("contoso.onmicrosoft.com", config.TenantId);
    }

    [Fact]
    public void CurrentAndNextServerCertificates_AreBothKeptOnceEach()
    {
        using var next = CertificateFactory.CreateServerCertificate(Host, Now);
        var document = Document() with { ServerCertificates = [Base64(_server), Base64(next), Base64(_server)] };

        Assert.True(ProxyConfigValidator.TryValidate(document, Now, out var config, out var error), error);

        Assert.Equal([_server.Thumbprint, next.Thumbprint], config.ServerCertificates.Select(certificate => certificate.Thumbprint));
    }

    [Fact]
    public void AnExpiredServerCertificateBesideAValidOne_IsDropped()
    {
        using var expired = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-400));
        var document = Document() with { ServerCertificates = [Base64(expired), Base64(_server)] };

        Assert.True(ProxyConfigValidator.TryValidate(document, Now, out var config, out var error), error);

        Assert.Equal(_server.Thumbprint, Assert.Single(config.ServerCertificates).Thumbprint);
    }

    // ---- Format and parse ----------------------------------------------------------------

    [Theory]
    [InlineData("dev-conduit/2")]
    [InlineData("DEV-CONDUIT/1")]
    [InlineData("POC-DEVPROXY/1")]
    [InlineData("poc-devproxy/2")]
    [InlineData("")]
    public void AnotherFormat_IsRefused(string format) =>
        Assert.Contains("format", Refusal(Document() with { Format = format }));

    [Theory]
    [InlineData(ProxyConfigDocument.CurrentFormat)]
    [InlineData(ProxyConfigDocument.LegacyFormat)]
    public void TheCurrentAndTheLegacyFormat_AreAccepted(string format)
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { Format = format }, Now, out _, out var error), error);
    }

    [Fact]
    public void TheFormatNames_AreTheProductsCurrentAndPreviousNames()
    {
        Assert.Equal("dev-conduit/1", ProxyConfigDocument.CurrentFormat);
        Assert.Equal("poc-devproxy/1", ProxyConfigDocument.LegacyFormat);
    }

    [Fact]
    public void ALegacyFile_ParsesAndValidatesFromItsBytes()
    {
        var json = JsonNode.Parse(Document().ToUtf8())!.AsObject();
        json["format"] = ProxyConfigDocument.LegacyFormat;

        Assert.True(ProxyConfigValidator.TryValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, out _, out var error), error);
    }

    [Fact]
    public void AnUnknownProperty_IsRefused()
    {
        var json = JsonNode.Parse(Document().ToUtf8())!.AsObject();
        json["extra"] = "surprise";

        Assert.StartsWith("The file is not a valid .conduit:", Refusal(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ broken")]
    [InlineData("[]")]
    public void TextThatIsNotADocument_IsRefused(string text) =>
        Assert.StartsWith("The file is not a valid .conduit:", Refusal(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Parse_OfNullThrowsJsonException() =>
        Assert.Throws<JsonException>(() => ProxyConfigDocument.Parse("null"u8));

    [Fact]
    public void FileNameFor_IsTheNormalizedHostWithTheExtension()
    {
        Assert.Equal($"{Host}.conduit", ProxyConfigDocument.FileNameFor(" DevConduit.Example.Internal. "));
        Assert.Equal(".conduit", ProxyConfigDocument.FileExtension);
        Assert.Equal(1024 * 1024, ProxyConfigDocument.MaxBytes);
    }

    // ---- Fields --------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.5")]
    [InlineData("*.devconduit.internal")]
    [InlineData("under_score.internal")]
    public void AHostnameThatIsNotADnsName_IsRefused(string hostname) =>
        Assert.Contains("is not a DNS host name", Refusal(Document() with { Hostname = hostname }));

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.5:8091")]
    [InlineData("http://10.0.0.5")]
    [InlineData("bad_name")]
    public void AnUnusableAddress_IsRefused(string address) =>
        Assert.Contains("is not an IP address or a host name", Refusal(Document() with { Address = address }));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void APortOutOfRange_IsRefused(int port) =>
        Assert.Contains("is not a TCP port", Refusal(Document() with { Port = port }));

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("has space")]
    [InlineData("slash/")]
    public void AnUnusableRemoteName_IsRefused(string remote) =>
        Assert.Contains("remote name", Refusal(Document() with { Remote = remote }));

    [Fact]
    public void ARemoteNameOfSixtyFiveCharacters_IsRefused() =>
        Assert.Contains("remote name", Refusal(Document() with { Remote = new string('r', 65) }));

    [Theory]
    [InlineData("", Client)]
    [InlineData("not a tenant", Client)]
    [InlineData(Tenant, "")]
    [InlineData(Tenant, "contoso.onmicrosoft.com")]
    [InlineData(Tenant, "<client-id>")]
    public void UnusableEntraIds_AreRefused(string tenant, string client) =>
        Assert.Contains("Entra tenant id and client id", Refusal(Document() with { TenantId = tenant, ClientId = client }));

    [Theory]
    [InlineData("a:b", "https://api.internal")]
    [InlineData("", "https://api.internal")]
    [InlineData("ok", "ftp://api.internal")]
    [InlineData("ok", "https://api.internal/path")]
    [InlineData("ok", "https://user:pass@api.internal")]
    [InlineData("ok", "not a uri")]
    public void AnUnusableRoute_IsRefused(string name, string target) =>
        Assert.Contains("route", Refusal(Document() with { Routes = new Dictionary<string, string> { [name] = target } }));

    [Fact]
    public void ARouteNamedTwiceInDifferentCase_IsRefused()
    {
        var json = JsonNode.Parse(Document().ToUtf8())!.AsObject();
        json["routes"] = new JsonObject { ["api"] = "https://a.internal", ["API"] = "https://b.internal" };

        Assert.Contains("appears twice", Refusal(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    // ---- Server certificates -------------------------------------------------------------

    [Fact]
    public void AHostnameNotInTheServerCertificatesNames_IsRefused()
    {
        var error = Refusal(Document() with { Hostname = "other.devconduit.internal" });

        Assert.Contains("does not name other.devconduit.internal", error);
    }

    [Fact]
    public void AServerCertificateSignedByAnAuthority_IsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Host}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CertificateFactory.ServerAuthOid)], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Host);
        request.CertificateExtensions.Add(names.Build());
        using var issued = request.Create(_authority, Now.AddDays(-1), Now.AddDays(100), CertificateFactory.NewSerialNumber());

        Assert.Contains("not self-signed", Refusal(Document() with { ServerCertificates = [Base64(issued)] }));
    }

    [Fact]
    public void AServerCertificateWithABadSignature_IsRefused()
    {
        using var forged = TestCertificates.SelfSigned(Host, [Host], signWithAnotherKey: true);

        Assert.Contains("not self-signed", Refusal(Document() with { ServerCertificates = [Base64(forged)] }));
    }

    [Fact]
    public void ACertificateAuthorityAsServerCertificate_IsRefused()
    {
        using var authority = TestCertificates.SelfSigned(Host, [Host], authority: true);

        Assert.Contains("CA=true", Refusal(Document() with { ServerCertificates = [Base64(authority)] }));
    }

    [Fact]
    public void OneBadServerCertificateAmongGoodOnes_RefusesTheFile()
    {
        using var authority = TestCertificates.SelfSigned(Host, [Host], authority: true);

        Assert.Contains("CA=true", Refusal(Document() with { ServerCertificates = [Base64(_server), Base64(authority)] }));
    }

    [Fact]
    public void AServerCertificateWithoutServerAuth_IsRefused()
    {
        using var noUsage = TestCertificates.SelfSigned(Host, [Host], withUsageExtension: false);

        Assert.Contains("not for server authentication", Refusal(Document() with { ServerCertificates = [Base64(noUsage)] }));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AQID")]
    [InlineData("")]
    public void AMalformedServerCertificate_IsRefused(string encoded) =>
        Assert.Contains("not a DER certificate", Refusal(Document() with { ServerCertificates = [encoded] }));

    [Fact]
    public void NoServerCertificates_IsRefused() =>
        Assert.Contains("1 to 4 server certificates", Refusal(Document() with { ServerCertificates = [] }));

    [Fact]
    public void FiveServerCertificates_IsRefused() =>
        Assert.Contains("1 to 4 server certificates", Refusal(Document() with { ServerCertificates = Enumerable.Repeat(Base64(_server), 5).ToList() }));

    [Fact]
    public void OnlyExpiredServerCertificates_IsRefused()
    {
        using var expired = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-400));

        Assert.Contains("has expired", Refusal(Document() with { ServerCertificates = [Base64(expired)] }));
    }

    // ---- Client certificate --------------------------------------------------------------

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AQID")]
    [InlineData("")]
    public void AMalformedClientCertificate_IsRefused(string encoded) =>
        Assert.Contains("not a PKCS#12 certificate", Refusal(Document() with { ClientCertificate = encoded }));

    [Fact]
    public void AClientCertificateWithoutClientAuth_IsRefused()
    {
        // The server's own certificate, with its key: server authentication only.
        var error = Refusal(Document() with { ClientCertificate = Convert.ToBase64String(CertificateFactory.ExportPkcs12(_server)) });

        Assert.Contains("not for client authentication", error);
    }

    [Fact]
    public void AClientCertificateWithoutItsKey_IsRefused()
    {
        using var publicOnly = TestCertificates.PublicOnly(_client);

        var error = Refusal(Document() with { ClientCertificate = Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12)) });

        Assert.Contains("no private key", error);
    }

    [Fact]
    public void AnAuthorityAsClientCertificate_IsRefused() =>
        Assert.Contains("certificate authority", Refusal(Document() with { ClientCertificate = Convert.ToBase64String(CertificateFactory.ExportPkcs12(_authority)) }));

    [Fact]
    public void APasswordProtectedClientCertificate_IsRefused()
    {
        var protectedPfx = _client.Export(X509ContentType.Pkcs12, "secret");

        Assert.Contains("not a PKCS#12 certificate without a password", Refusal(Document() with { ClientCertificate = Convert.ToBase64String(protectedPfx) }));
    }

    [Fact]
    public void AnExpiredClientCertificate_IsRefused()
    {
        using var oldAuthority = CertificateFactory.CreateClientAuthority(Host, Now.AddDays(-500));
        using var old = CertificateFactory.CreateClientCertificate(oldAuthority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now.AddDays(-400));

        Assert.Contains("expired on", Refusal(Document() with { ClientCertificate = Convert.ToBase64String(CertificateFactory.ExportPkcs12(old)) }));
    }

    // ---- IsName --------------------------------------------------------------------------

    [Theory]
    [InlineData("api", true)]
    [InlineData("A.b-c_d9", true)]
    [InlineData(" padded ", true)]
    [InlineData("a:b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("é", false)]
    public void IsName_IsOneToSixtyFourKeySafeCharacters(string? value, bool expected) =>
        Assert.Equal(expected, ProxyConfigValidator.IsName(value));
}
