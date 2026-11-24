using System.Runtime.Versioning;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// The <c>export</c> and <c>certs revoke</c> commands as the elevated helper calls them, with
/// settings passed as arguments, empty throwaway settings files, and a throwaway data root
/// given explicitly every time, so nothing under <c>%ProgramData%</c> is read or written even
/// on a machine where the service is installed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CertificateCommandsTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";

    private readonly SettingsLayout _layout = new();
    private readonly string _dataRoot;
    private readonly string _outDirectory;

    public CertificateCommandsTests()
    {
        _dataRoot = Path.Combine(_layout.Root, "data");
        _outDirectory = Path.Combine(_layout.Root, "out");
        Directory.CreateDirectory(_outDirectory);
    }

    public void Dispose() => _layout.Dispose();

    private string[] Arguments(params string[] extra) =>
    [
        $"--DevConduit:DataRoot={_dataRoot}",
        "--DevConduit:Mode=server",
        $"--DevConduit:Server:Hostname={Host}",
        $"--AzureAd:TenantId={Tenant}",
        $"--AzureAd:ClientId={Client}",
        "--DevConduit:Routes:api=https://api.internal:8443",
        .. extra
    ];

    private ServiceAdminExitCode Export(string? output, string? address, string[] arguments, bool qualified, StringWriter writer) =>
        ExportCommand.Export(output, address, arguments, qualified, _layout.Files, loggers: null, writer);

    // ---- Export: refusals ----------------------------------------------------------------

    [Theory]
    [InlineData("--DevConduit:Server:Hostname=", "DevConduit:Server:Hostname is not set")]
    [InlineData("--DevConduit:Server:Hostname=10.0.0.5", "is not a DNS host name")]
    [InlineData("--DevConduit:Server:Port=70000", "is not a TCP port")]
    [InlineData("--Kestrel:Endpoints:Https:Url=https://0.0.0.0:443", "Kestrel:Endpoints is configured")]
    [InlineData("--AzureAd:TenantId=<tenant-id>", "AzureAd:TenantId and AzureAd:ClientId must be set")]
    [InlineData("--AzureAd:ClientId=", "AzureAd:TenantId and AzureAd:ClientId must be set")]
    [InlineData("--DevConduit:Mode=local", "configured for local mode")]
    public void Export_IsRefusedWhenTheServerCannotExport(string setting, string expected)
    {
        using var writer = new StringWriter();

        var code = Export(Path.Combine(_outDirectory, "x.conduit"), "10.0.0.5", Arguments(setting), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.ExportRefused, code);
        Assert.Contains(expected, writer.ToString());
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public void Export_WithoutAnAddress_IsRefused()
    {
        using var writer = new StringWriter();

        var code = Export(Path.Combine(_outDirectory, "x.conduit"), null, Arguments(), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.ExportRefused, code);
        Assert.Contains("No client address", writer.ToString());
    }

    [Fact]
    public void Export_WithAnUnusableAddress_IsRefused()
    {
        using var writer = new StringWriter();

        var code = Export(Path.Combine(_outDirectory, "x.conduit"), "http://10.0.0.5", Arguments(), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.ExportRefused, code);
        Assert.Contains("is not an IP address or host name", writer.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.conduit")]
    [InlineData("OUT\\x.txt")]
    [InlineData("OUT\\x.proxyconfig")]
    [InlineData("MISSING\\x.conduit")]
    [InlineData("OUT")]
    public void Export_ForTheHelper_NeedsAQualifiedConduitPathInAnExistingFolder(string? output)
    {
        using var writer = new StringWriter();
        var path = output?.Replace("OUT", _outDirectory, StringComparison.Ordinal)
            .Replace("MISSING", Path.Combine(_layout.Root, "missing"), StringComparison.Ordinal);

        var code = Export(path, "10.0.0.5", Arguments(), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.InvalidArguments, code);
        Assert.Contains("must name a .conduit file in an existing folder", writer.ToString());
        Assert.Empty(Directory.GetFiles(_outDirectory));
    }

    [Fact]
    public void Export_RefusesToWriteOverADirectoryNamedLikeTheFile()
    {
        using var writer = new StringWriter();
        var path = Path.Combine(_outDirectory, "dir.conduit");
        Directory.CreateDirectory(path);

        Assert.Equal(ServiceAdminExitCode.InvalidArguments, Export(path, "10.0.0.5", Arguments(), qualified: true, writer));
    }

    [Theory]
    [InlineData(true, "OUT\\dir.conduit")]
    [InlineData(true, "MISSING\\x.conduit")]
    [InlineData(false, "MISSING\\x.conduit")]
    [InlineData(true, "OUT\\x.json")]
    [InlineData(false, "OUT\\x.json")]
    [InlineData(false, "OUT\\x")]
    public void Export_ChecksTheOutputPathBeforeIssuingAnything(bool qualified, string output)
    {
        using var writer = new StringWriter();
        Directory.CreateDirectory(Path.Combine(_outDirectory, "dir.conduit"));
        var path = output.Replace("OUT", _outDirectory, StringComparison.Ordinal)
            .Replace("MISSING", Path.Combine(_layout.Root, "missing"), StringComparison.Ordinal);

        Assert.Equal(ServiceAdminExitCode.InvalidArguments, Export(path, "10.0.0.5", Arguments(), qualified, writer));

        // Nothing was issued: the data root was not even created.
        Assert.False(Directory.Exists(_dataRoot));
        Assert.Empty(Directory.GetFiles(_outDirectory));
        Assert.False(Directory.Exists(Path.Combine(_layout.Root, "missing")));
    }

    [Fact]
    public void Export_FromTheCommandLine_IntoAFolderHoldingADirectoryNamedLikeTheDefault_IsRefused()
    {
        using var writer = new StringWriter();
        Directory.CreateDirectory(Path.Combine(_outDirectory, $"{Host}.conduit"));

        Assert.Equal(ServiceAdminExitCode.InvalidArguments, Export(_outDirectory, "10.0.0.5", Arguments(), qualified: false, writer));
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public void Export_ThatCannotWriteTheFile_RevokesTheEnrollmentItIssuedAndLeavesNoPartialFile()
    {
        using var writer = new StringWriter();
        var path = Path.Combine(_outDirectory, "locked.conduit");
        File.WriteAllText(path, "earlier");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(ServiceAdminExitCode.ExportFailed, Export(path, "10.0.0.5", Arguments(), qualified: true, writer));

            Assert.Equal("earlier", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(_outDirectory));
            var ledger = ServerCertificateStore.ForDataRoot(_dataRoot).ReadIssued();
            var orphan = Assert.Single(ledger.Records);
            Assert.True(orphan.Revoked);
            var enrollment = ledger.FindEnrollment(orphan.Enrollment)!;
            Assert.True(enrollment.Revoked);
            Assert.Equal(ServerCertificateAuthority.UndeliveredRevocation, enrollment.RevokedReason);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ---- Export: success -----------------------------------------------------------------

    [Fact]
    public void Export_WritesAFileThatValidatesUnderTheGivenDataRoot()
    {
        using var writer = new StringWriter();
        var path = Path.Combine(_outDirectory, "client.conduit");

        var code = Export(path, "10.0.0.5", Arguments(), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.Success, code);
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out var error), error);
        Assert.Equal(Host, config.Hostname);
        Assert.Equal(8091, config.Port);
        Assert.Equal([new KeyValuePair<string, string>("api", "https://api.internal:8443")], config.Routes);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "certs", "index.json")));
        Assert.Contains($"certs revoke {config.ClientCertificate.SerialNumber}", writer.ToString());
    }

    [Fact]
    public void Export_WritesTheDefaultScopeWhenNoneIsConfigured()
    {
        using var writer = new StringWriter();
        var path = Path.Combine(_outDirectory, "client.conduit");

        Assert.Equal(ServiceAdminExitCode.Success, Export(path, "10.0.0.5", Arguments(), qualified: true, writer));

        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out var error), error);
        Assert.Equal("DevConduit.Forward", config.Scope);
        Assert.Contains("scope DevConduit.Forward", writer.ToString());
    }

    [Fact]
    public void Export_WritesTheServersFirstAcceptedScope()
    {
        using var writer = new StringWriter();
        var path = Path.Combine(_outDirectory, "client.conduit");

        var code = Export(path, "10.0.0.5", Arguments("--AzureAd:Scopes:0=DevProxy.Forward", "--AzureAd:Scopes:1=DevConduit.Forward"), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.Success, code);
        Assert.Equal("DevProxy.Forward", (string?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["scope"]);
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out var error), error);
        Assert.Equal("DevProxy.Forward", config.Scope);
    }

    [Fact]
    public void Export_IsRefusedWhenTheScopeIsNotAPlainName()
    {
        using var writer = new StringWriter();

        var code = Export(
            Path.Combine(_outDirectory, "x.conduit"), "10.0.0.5", Arguments($"--AzureAd:Scopes:0=api://{Client}/DevProxy.Forward"), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.ExportRefused, code);
        Assert.Contains("is not a plain scope name", writer.ToString());
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public void Export_FromTheCommandLine_DefaultsTheFileNameInsideAFolder()
    {
        using var writer = new StringWriter();

        var code = Export(_outDirectory, null, Arguments("--DevConduit:Server:ClientAddress=10.0.0.7"), qualified: false, writer);

        Assert.Equal(ServiceAdminExitCode.Success, code);
        var path = Path.Combine(_outDirectory, $"{Host}.conduit");
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out var error), error);
        Assert.Equal("10.0.0.7", config.Address);
    }

    [Fact]
    public void Export_SkipsARouteWhoseNameAClientCannotConfigure()
    {
        using var writer = new StringWriter();
        var path = Path.Combine(_outDirectory, "client.conduit");

        var code = Export(path, "10.0.0.5", Arguments("--DevConduit:Routes:bad name=https://bad.internal"), qualified: true, writer);

        Assert.Equal(ServiceAdminExitCode.Success, code);
        Assert.Contains("Route 'bad name' is not exported", writer.ToString());
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out _));
        Assert.Equal(["api"], config.Routes.Select(route => route.Key));
    }

    // ---- certs revoke --------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("0A:1B")]
    public void Revoke_OfSomethingNotASerial_IsInvalidArguments(string serial)
    {
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.InvalidArguments, CertsCommand.Revoke(serial, Arguments(), _layout.Files, writer));
        Assert.Contains("is not a certificate serial number", writer.ToString());
    }

    [Fact]
    public void Revoke_WithoutACertificateFolder_IsNotFound()
    {
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.NotFound, CertsCommand.Revoke("0A1B", Arguments(), _layout.Files, writer));
        Assert.Contains("There is no certificate folder", writer.ToString());
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public void Revoke_OfAnUnknownSerial_IsNotFound()
    {
        using var export = new StringWriter();
        Assert.Equal(ServiceAdminExitCode.Success, Export(Path.Combine(_outDirectory, "c.conduit"), "10.0.0.5", Arguments(), qualified: true, export));
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.NotFound, CertsCommand.Revoke("0A1B", Arguments(), _layout.Files, writer));
        Assert.Contains("No client certificate with serial 0A1B", writer.ToString());
    }

    [Fact]
    public void Revoke_OfAnExportedCertificate_MarksItRevoked()
    {
        using var export = new StringWriter();
        var path = Path.Combine(_outDirectory, "c.conduit");
        Assert.Equal(ServiceAdminExitCode.Success, Export(path, "10.0.0.5", Arguments(), qualified: true, export));
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out _));
        var serial = config.ClientCertificate.SerialNumber;
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.Success, CertsCommand.Revoke(serial.ToLowerInvariant(), Arguments(), _layout.Files, writer));

        var ledger = ServerCertificateStore.ForDataRoot(_dataRoot).ReadIssued();
        var record = ledger.Find(serial)!;
        Assert.True(record.Revoked);
        Assert.True(ledger.FindEnrollment(record.Enrollment)!.Revoked);
        Assert.Contains($"Revoked enrollment {record.Enrollment}: {serial}", writer.ToString());
    }

    private (string Serial, string Enrollment) ExportOne(string name)
    {
        using var export = new StringWriter();
        var path = Path.Combine(_outDirectory, name);
        Assert.Equal(ServiceAdminExitCode.Success, Export(path, "10.0.0.5", Arguments(), qualified: true, export));
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(path), DateTimeOffset.UtcNow, out var config, out _));
        var serial = config.ClientCertificate.SerialNumber;
        return (serial, ServerCertificateStore.ForDataRoot(_dataRoot).ReadIssued().Find(serial)!.Enrollment!);
    }

    [Fact]
    public void RevokeEnrollment_RevokesItByIdAndLeavesTheOthers()
    {
        var first = ExportOne("a.conduit");
        var second = ExportOne("b.conduit");
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.Success, CertsCommand.RevokeEnrollment(first.Enrollment.ToUpperInvariant(), Arguments(), _layout.Files, writer));

        var ledger = ServerCertificateStore.ForDataRoot(_dataRoot).ReadIssued();
        Assert.False(ledger.IsAccepted(first.Serial));
        Assert.True(ledger.IsAccepted(second.Serial));
        Assert.Contains($"Revoked enrollment {first.Enrollment}", writer.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0A1B")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void RevokeEnrollment_OfSomethingNotAnId_IsInvalidArguments(string id)
    {
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.InvalidArguments, CertsCommand.RevokeEnrollment(id, Arguments(), _layout.Files, writer));
        Assert.Contains("is not an enrollment id", writer.ToString());
    }

    [Fact]
    public void RevokeEnrollment_OfAnUnknownId_IsNotFound()
    {
        ExportOne("a.conduit");
        using var writer = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.NotFound, CertsCommand.RevokeEnrollment("0123456789abcdef0123456789abcdef", Arguments(), _layout.Files, writer));
        Assert.Contains("No enrollment", writer.ToString());
    }

    [Fact]
    public void List_GroupsByEnrollmentWithTheLatestCertificateAndItsState()
    {
        var first = ExportOne("a.conduit");
        var second = ExportOne("b.conduit");
        using (var revoke = new StringWriter())
        {
            CertsCommand.Revoke(second.Serial, Arguments(), _layout.Files, revoke);
        }

        using var writer = new StringWriter();
        Assert.Equal(ServiceAdminExitCode.Success, CertsCommand.List(Arguments(), _layout.Files, writer));

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var firstLine = Assert.Single(lines, line => line.StartsWith($"Enrollment {first.Enrollment}", StringComparison.Ordinal));
        Assert.Contains("active", firstLine);
        var secondLine = Assert.Single(lines, line => line.StartsWith($"Enrollment {second.Enrollment}", StringComparison.Ordinal));
        Assert.Contains($"({ServerCertificateAuthority.AdministratorRevocation})", secondLine);
        Assert.Contains(lines, line => line.Contains($"latest {first.Serial}", StringComparison.Ordinal) && line.Contains("1 certificate in all", StringComparison.Ordinal));
        Assert.DoesNotContain("retired authority", writer.ToString());
    }

    [Fact]
    public void List_AfterAHostnameChange_ReportsTheOldAuthoritysEnrollmentsAsRevoked()
    {
        var first = ExportOne("a.conduit");
        using (var export = new StringWriter())
        {
            Assert.Equal(
                ServiceAdminExitCode.Success,
                Export(Path.Combine(_outDirectory, "renamed.conduit"), "10.0.0.5", Arguments("--DevConduit:Server:Hostname=renamed.devconduit.internal"), qualified: true, export));
        }

        using var writer = new StringWriter();
        Assert.Equal(ServiceAdminExitCode.Success, CertsCommand.List(Arguments("--DevConduit:Server:Hostname=renamed.devconduit.internal"), _layout.Files, writer));

        var line = Assert.Single(
            writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            candidate => candidate.StartsWith($"Enrollment {first.Enrollment}", StringComparison.Ordinal));
        Assert.Contains($"({ServerCertificateAuthority.HostnameChangedRevocation})", line);
        Assert.Contains($"retired authority", line);
        Assert.Contains(Host, line);
    }
}
