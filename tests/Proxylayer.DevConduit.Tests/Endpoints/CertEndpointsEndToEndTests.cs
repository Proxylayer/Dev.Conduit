using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Endpoints;

/// <summary>
/// The server built exactly as a launch builds it, in server mode with the app-managed
/// certificate for <c>localhost</c>, on a free loopback port, with throwaway settings files and
/// data root. A client certificate is exported, pinned trust is set up in a throwaway client
/// folder, and <c>/cert</c> is called through the production <see cref="CertApiClient"/>. No
/// network beyond loopback; the Entra ids are dummies that are never used.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CertEndpointsEndToEndTests
{
    private const string Host = "localhost";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>An HTTP/1.1 client that pins through the policy and presents <paramref name="identity"/>, if any.</summary>
    private static HttpClient RawClient(RemoteCertificatePolicy policy, X509Certificate2? identity)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = policy.Validate;
        if (identity is not null)
        {
            handler.SslOptions.ClientCertificates = [identity];
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => identity;
        }

        return new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Sets up the server's store as it stood <paramref name="daysAgo"/> days ago: its authority
    /// and server certificate, and a client certificate issued then in an enrollment of its own,
    /// which is now inside its last sixty days. The server converges from there when it starts.
    /// </summary>
    private static IssuedClientCertificate IssueInThePast(string dataRoot, int daysAgo)
    {
        var past = new ServerCertificateAuthority(
            ServerCertificateStore.ForDataRoot(dataRoot), new ManualClock(DateTimeOffset.UtcNow.AddDays(-daysAgo)), NullLogger.Instance);
        var set = past.Converge(Host);
        set.Current.Dispose();
        set.Next?.Dispose();
        set.ClientAuthority.Dispose();
        return past.IssueClientCertificate(Host);
    }

    [Fact]
    public async Task Cert_AuthenticatesPublishesRenewsAndRefusesARevokedEnrollment()
    {
        var stopwatch = Stopwatch.StartNew();
        using var layout = new SettingsLayout();
        var dataRoot = Path.Combine(layout.Root, "data");
        var port = FreePort();
        var baseAddress = new Uri($"https://{Host}:{port}/");

        var aged = IssueInThePast(dataRoot, daysAgo: 310);
        using var agedIdentity = CertificateFactory.LoadPkcs12(CertificateFactory.ExportPkcs12(aged.Certificate));
        aged.Certificate.Dispose();

        string[] args =
        [
            "--mode", "server",
            $"--AzureAd:TenantId={Tenant}",
            $"--AzureAd:ClientId={Client}",
            $"--DevConduit:DataRoot={dataRoot}",
            $"--DevConduit:Server:Hostname={Host}",
            $"--DevConduit:Server:Port={port}",
            "--DevConduit:Server:TrustStore=None"
        ];

        Assert.True(
            DevConduitApplication.TryBuild(args, runningAsService: false, layout.Files, NullLoggerProvider.Instance, out var app, out var fatal),
            fatal);

        // The managed endpoint listens on every interface; a test keeps to loopback. Kestrel
        // reads its endpoints when it starts, so this one value is narrowed after the build.
        Assert.Equal($"https://0.0.0.0:{port}", app.Configuration["Kestrel:Endpoints:ManagedHttps:Url"]);
        app.Configuration["Kestrel:Endpoints:ManagedHttps:Url"] = $"https://{Host}:{port}";

        await using (app)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await app.StartAsync(cancel.Token);
            try
            {
                var authority = app.Services.GetRequiredService<ServerCertificateAuthority>();

                // Export a client configuration and set up pinned trust from it, as an import would.
                var export = new ProxyConfigExporter(authority, TimeProvider.System).Export(
                    new ProxyConfigExportRequest(Host, "127.0.0.1", port, Tenant, Client, []),
                    Path.Combine(layout.Root, "localhost.conduit"));
                Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(export.Path), DateTimeOffset.UtcNow, out var config, out var error), error);

                var clientDirectory = new ClientCertificateDirectory(Path.Combine(layout.Root, "client"));
                var folder = clientDirectory.Folder(Host);
                folder.WriteTrust([.. config.ServerCertificates.Select(PinnedTrust.Pin)]);
                folder.WriteIdentity(config.ClientCertificatePkcs12);
                var policy = new RemoteCertificatePolicy(new ClientTrustStore(ClientTrustLayers.MachineOnly(clientDirectory)), TimeProvider.System);
                using var identity = folder.ReadIdentity()!;

                // Without a client certificate: 401.
                using (var anonymous = RawClient(policy, identity: null))
                {
                    using var response = await anonymous.GetAsync(new Uri(baseAddress, "cert"), cancel.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
                }

                // With the exported certificate: 200 and the published certificates.
                using (var raw = RawClient(policy, identity))
                {
                    using var response = await raw.GetAsync(new Uri(baseAddress, "cert"), cancel.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel.Token))!.AsObject();
                    Assert.Equal(["hostname", "serverCertificates", "clientCertificate"], json.Select(property => property.Key));
                    Assert.Equal(["notAfter", "serial", "renewAfter"], json["clientCertificate"]!.AsObject().Select(property => property.Key));
                    Assert.Equal(["der", "notBefore", "notAfter", "thumbprint"], json["serverCertificates"]![0]!.AsObject().Select(property => property.Key));
                }

                CertResponse answer;
                using (var api = new CertApiClient(baseAddress, identity, policy))
                {
                    answer = await api.GetAsync(cancel.Token);
                }

                Assert.Equal(Host, answer.Hostname);
                Assert.Equal(config.ServerCertificates.Select(certificate => certificate.Thumbprint), answer.ServerCertificates.Select(certificate => certificate.Thumbprint));
                Assert.Equal(identity.SerialNumber, answer.ClientCertificate.Serial);
                Assert.Equal(new DateTimeOffset(identity.NotAfter.ToUniversalTime()), answer.ClientCertificate.NotAfter);
                Assert.Equal(answer.ClientCertificate.NotAfter - TimeSpan.FromDays(60), answer.ClientCertificate.RenewAfter);

                // The exported certificate has a year left: renewing it now is a bare 409, and nothing is issued.
                using (var api = new CertApiClient(baseAddress, identity, policy))
                {
                    using var earlyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var early = await Assert.ThrowsAsync<HttpRequestException>(
                        () => api.RenewAsync(CertificateFactory.CreateSigningRequest(identity.SubjectName, earlyKey), cancel.Token));
                    Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
                }

                Assert.Single(authority.ReadIssued().CertificatesOf(export.ClientCertificate.Enrollment!));

                // The aged certificate, 55 days left: renewal from a signing request gives a new
                // certificate for the request's key, in the same enrollment.
                using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                CertRenewResponse renewal;
                using (var api = new CertApiClient(baseAddress, agedIdentity, policy))
                {
                    renewal = await api.RenewAsync(CertificateFactory.CreateSigningRequest(agedIdentity.SubjectName, newKey), cancel.Token);
                }

                using var renewedPublic = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(renewal.Certificate));
                Assert.NotEqual(agedIdentity.SerialNumber, renewal.Serial);
                Assert.Equal(renewedPublic.SerialNumber, renewal.Serial);
                Assert.Equal(agedIdentity.Subject, renewedPublic.Subject);
                Assert.Equal(agedIdentity.Issuer, renewedPublic.Issuer);
                Assert.Equal(aged.Record.Enrollment, ClientEnrollmentId.Of(renewedPublic.SubjectName));
                Assert.True(CertificateFactory.HasUsage(renewedPublic, CertificateFactory.ClientAuthOid));
                using (var renewedKey = renewedPublic.GetECDsaPublicKey()!)
                {
                    Assert.Equal(newKey.ExportSubjectPublicKeyInfo(), renewedKey.ExportSubjectPublicKeyInfo());
                }

                Assert.Equal(renewal.Serial, authority.ReadIssued().Find(agedIdentity.SerialNumber)!.SupersededBy);
                Assert.Equal(aged.Record.Enrollment, authority.ReadIssued().Find(renewal.Serial)!.Enrollment);

                using var renewedWithKey = renewedPublic.CopyWithPrivateKey(newKey);
                using var renewedIdentity = CertificateFactory.LoadPkcs12(CertificateFactory.ExportPkcs12(renewedWithKey));

                // The superseded certificate still authenticates, and so does its renewal.
                using (var api = new CertApiClient(baseAddress, agedIdentity, policy))
                {
                    Assert.Equal(agedIdentity.SerialNumber, (await api.GetAsync(cancel.Token)).ClientCertificate.Serial);
                }

                using (var api = new CertApiClient(baseAddress, renewedIdentity, policy))
                {
                    Assert.Equal(renewal.Serial, (await api.GetAsync(cancel.Token)).ClientCertificate.Serial);
                }

                // A garbage body is a bare 400, after the certificate is checked.
                using (var raw = RawClient(policy, agedIdentity))
                {
                    using var content = new StringContent("{ \"csr\": \"not base64!\" }");
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await raw.PostAsync(new Uri(baseAddress, "cert/renew"), content, cancel.Token);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }

                // A request for another enrollment's subject is a bare 400.
                using (var api = new CertApiClient(baseAddress, agedIdentity, policy))
                {
                    using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var mismatch = await Assert.ThrowsAsync<HttpRequestException>(
                        () => api.RenewAsync(CertificateFactory.CreateSigningRequest(identity.SubjectName, otherKey), cancel.Token));
                    Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
                }

                // Revoking the original serial revokes its enrollment: the renewal made before the
                // revoke is refused too, for /cert and for another renewal alike.
                var revocation = authority.Revoke(agedIdentity.SerialNumber);
                Assert.Equal(aged.Record.Enrollment, revocation!.Enrollment);
                using (var api = new CertApiClient(baseAddress, agedIdentity, policy))
                {
                    var refused = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAsync(cancel.Token));
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                }

                using (var api = new CertApiClient(baseAddress, renewedIdentity, policy))
                {
                    var refused = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAsync(cancel.Token));
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

                    using var laterKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var renewRefused = await Assert.ThrowsAsync<HttpRequestException>(
                        () => api.RenewAsync(CertificateFactory.CreateSigningRequest(renewedIdentity.SubjectName, laterKey), cancel.Token));
                    Assert.Equal(HttpStatusCode.Forbidden, renewRefused.StatusCode);
                }

                // The exported certificate is another enrollment and still works until its own is revoked.
                using (var api = new CertApiClient(baseAddress, identity, policy))
                {
                    Assert.Equal(identity.SerialNumber, (await api.GetAsync(cancel.Token)).ClientCertificate.Serial);
                }

                Assert.NotNull(authority.RevokeEnrollment(export.ClientCertificate.Enrollment!));
                using (var api = new CertApiClient(baseAddress, identity, policy))
                {
                    var refused = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAsync(cancel.Token));
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                }
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }

        TestContext.Current.SendDiagnosticMessage($"End-to-end /cert test took {stopwatch.Elapsed.TotalSeconds:N1}s.");
    }

    [Fact]
    public async Task Cert_RefusesACertificateFromAnotherAuthority()
    {
        using var layout = new SettingsLayout();
        var port = FreePort();
        var baseAddress = new Uri($"https://{Host}:{port}/");
        string[] args =
        [
            "--mode", "server",
            $"--AzureAd:TenantId={Tenant}",
            $"--AzureAd:ClientId={Client}",
            $"--DevConduit:DataRoot={Path.Combine(layout.Root, "data")}",
            $"--DevConduit:Server:Hostname={Host}",
            $"--DevConduit:Server:Port={port}",
            "--DevConduit:Server:TrustStore=None"
        ];

        Assert.True(DevConduitApplication.TryBuild(args, false, layout.Files, NullLoggerProvider.Instance, out var app, out var fatal), fatal);
        app.Configuration["Kestrel:Endpoints:ManagedHttps:Url"] = $"https://{Host}:{port}";

        await using (app)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await app.StartAsync(cancel.Token);
            try
            {
                var set = app.Services.GetRequiredService<ServerCertificateManager>().Certificates;
                var clientDirectory = new ClientCertificateDirectory(Path.Combine(layout.Root, "client"));
                clientDirectory.Folder(Host).WriteTrust([.. set.Published.Select(PinnedTrust.Pin)]);
                var policy = new RemoteCertificatePolicy(new ClientTrustStore(ClientTrustLayers.MachineOnly(clientDirectory)), TimeProvider.System);

                // A certificate from another authority with the same names: refused.
                using var foreignAuthority = CertificateFactory.CreateClientAuthority(Host, DateTimeOffset.UtcNow);
                using var foreign = CertificateFactory.CreateClientCertificate(
                    foreignAuthority, CertificateFactory.ClientSubject(Host, DateTimeOffset.UtcNow, TestCertificates.Enrollment), DateTimeOffset.UtcNow);
                using var usableForeign = CertificateFactory.LoadPkcs12(CertificateFactory.ExportPkcs12(foreign));

                using var api = new CertApiClient(baseAddress, usableForeign, policy);
                var refused = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAsync(cancel.Token));
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
    }
}
