using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// A server exports a <c>.conduit</c>, a client imports it twice and then forgets the
/// server. Every target is injected: a throwaway hosts file, client folder, machine settings
/// file and data root, and the current user's AddressBook store standing in for
/// <c>LocalMachine\Root</c>. Whatever lands in that store is removed afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxyConfigRoundTripTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";
    private const StoreName ScratchStore = StoreName.AddressBook;
    private const StoreLocation ScratchLocation = StoreLocation.CurrentUser;

    private const string OriginalHosts =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n127.0.0.1       localhost\r\n10.9.9.9 unrelated.example\r\n";

    private const string OriginalSettings = """
        {
          "AzureAd": { "TenantId": "", "ClientId": "" },
          "DevConduit": {
            "Mode": "local",
            "Remotes": { "other": "https://other.example:8091" },
            "Routes": { "keep": { "Target": "https://keep.internal", "Remote": "other" } }
          }
        }
        """;

    private readonly TempDirectory _root = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly ProxyConfigImportTargets _targets;
    private readonly ServerCertificateAuthority _authority;
    private readonly HashSet<string> _thumbprints = new(StringComparer.OrdinalIgnoreCase);

    public ProxyConfigRoundTripTests()
    {
        var hostsPath = Path.Combine(_root.Path, "etc", "hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(hostsPath)!);
        File.WriteAllBytes(hostsPath, Encoding.ASCII.GetBytes(OriginalHosts));

        var settingsPath = Path.Combine(_root.Path, "machine", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, OriginalSettings);

        _targets = new ProxyConfigImportTargets(
            new HostsFile(hostsPath),
            new ClientCertificateDirectory(Path.Combine(_root.Path, "client"), restrictFolders: false),
            settingsPath,
            ScratchStore,
            ScratchLocation,
            ProtectMachineSettings: false);

        _authority = new ServerCertificateAuthority(
            ServerCertificateStore.ForDataRoot(Path.Combine(_root.Path, "server-data")), _clock, NullLogger.Instance);
    }

    public void Dispose()
    {
        // Whatever the outcome, nothing this test added stays in the scratch store.
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

        _root.Dispose();
    }

    private ProxyConfigExportResult Export(string address = "10.0.0.5", string? path = null)
    {
        var request = new ProxyConfigExportRequest(
            Host, address, 8091, Tenant, Client,
            [new("api", "https://api.internal:8443"), new("web", "https://web.internal")]);
        var result = new ProxyConfigExporter(_authority, _clock).Export(request, path ?? Path.Combine(_root.Path, "out", ProxyConfigDocument.FileNameFor(Host)));
        foreach (var certificate in result.Document.ServerCertificates)
        {
            using var parsed = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificate));
            _thumbprints.Add(parsed.Thumbprint);
        }

        return result;
    }

    private ValidatedProxyConfig Validate(ProxyConfigExportResult export)
    {
        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(export.Path), _clock.GetUtcNow(), out var config, out var error), error);
        return config;
    }

    private IReadOnlyList<string> Import(ValidatedProxyConfig config) =>
        new ProxyConfigImporter(_targets, _clock).Import(config);

    private ImportedServerRemoval Forget(string hostname = Host) =>
        new ImportedServerRemover(_targets, _clock).Remove(hostname);

    private int InScratchStore(string thumbprint)
    {
        using var store = new X509Store(ScratchStore, ScratchLocation);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var count = found.Count;
        foreach (var certificate in found)
        {
            certificate.Dispose();
        }

        return count;
    }

    private JsonObject Settings() => SettingsJson.Parse(File.ReadAllText(_targets.MachineSettingsPath));

    // ---- Export --------------------------------------------------------------------------

    [Fact]
    public void Export_WritesAValidFileNamedForTheHost()
    {
        var export = Export();

        Assert.Equal(Path.Combine(_root.Path, "out", $"{Host}.conduit"), export.Path);
        var config = Validate(export);
        Assert.Equal(Host, config.Hostname);
        Assert.Equal("devconduit", config.Remote);
        Assert.Equal("10.0.0.5", config.Address);
        Assert.Equal(_clock.Now, export.Document.ExportedAt);
        Assert.Equal(export.ClientCertificate.Serial, config.ClientCertificate.SerialNumber);
        Assert.Equal(2, config.Routes.Count);
        Assert.True(_authority.ReadIssued().IsAccepted(export.ClientCertificate.Serial));
    }

    [Fact]
    public void Export_ProtectsTheFileForItsOwnerAndAdministratorsOnly()
    {
        var export = Export();

        var security = new FileInfo(export.Path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            new[] { identity.User!.Value, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value }.Order(StringComparer.Ordinal),
            sids);
    }

    [Fact]
    public void Export_IssuesANewClientCertificateEachTime()
    {
        var first = Export();
        var second = Export(path: Path.Combine(_root.Path, "out", "second.conduit"));

        Assert.NotEqual(first.ClientCertificate.Serial, second.ClientCertificate.Serial);
        Assert.Equal(first.Document.ServerCertificates, second.Document.ServerCertificates);
        Assert.Equal(2, _authority.ReadIssued().Records.Count);
    }

    // ---- Import --------------------------------------------------------------------------

    [Fact]
    public void Import_AppliesEveryPart()
    {
        var export = Export();
        var config = Validate(export);
        var server = config.ServerCertificates.Single();

        var summary = Import(config);

        // Hosts file.
        Assert.Equal(OriginalHosts + HostsFileText.ManagedLine("10.0.0.5", Host) + "\r\n", File.ReadAllText(_targets.HostsFile.Path));

        // Client folder.
        var folder = _targets.ClientDirectory.Folder(Host);
        Assert.Equal([server.Thumbprint], folder.ReadTrust().Select(pin => pin.Thumbprint));
        using (var identity = folder.ReadIdentity())
        {
            Assert.Equal(config.ClientCertificate.Thumbprint, identity!.Thumbprint);
            Assert.True(identity.HasPrivateKey);
        }

        // Windows trust, in the scratch store, and the record of what was added.
        Assert.Equal(1, InScratchStore(server.Thumbprint));
        var added = Assert.Single(new RootStoreTrust(_targets.ClientDirectory.RootStoreRecordPath, ScratchStore, ScratchLocation).AddedFor(Host));
        Assert.Equal(server.Thumbprint, added.Thumbprint);

        // Machine settings: AzureAd was blank, the remote and its routes are added, the rest kept.
        var settings = Settings();
        Assert.Equal(Tenant, (string?)settings["AzureAd"]!["TenantId"]);
        Assert.Equal(Client, (string?)settings["AzureAd"]!["ClientId"]);
        Assert.Equal("local", (string?)settings["DevConduit"]!["Mode"]);
        Assert.Equal($"https://{Host}:8091", (string?)settings["DevConduit"]!["Remotes"]!["devconduit"]!["Endpoint"]);
        Assert.Equal("https://other.example:8091", (string?)settings["DevConduit"]!["Remotes"]!["other"]);
        Assert.Equal(["keep", "api", "web"], settings["DevConduit"]!["Routes"]!.AsObject().Select(route => route.Key));

        // The import record.
        var record = Assert.Single(ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath));
        Assert.Equal(Host, record.Hostname);
        Assert.Equal("devconduit", record.Remote);
        Assert.Equal(["api", "web"], record.Routes);
        Assert.Equal("10.0.0.5", record.Address);

        Assert.Contains(summary, line => line.Contains("1 added", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_ASecondTime_ChangesNothingFurther()
    {
        var config = Validate(Export());
        Import(config);
        var hosts = File.ReadAllBytes(_targets.HostsFile.Path);
        var settings = File.ReadAllText(_targets.MachineSettingsPath);
        var trust = _targets.ClientDirectory.Folder(Host).ReadTrust();
        var server = config.ServerCertificates.Single();

        var summary = Import(config);

        Assert.Equal(hosts, File.ReadAllBytes(_targets.HostsFile.Path));
        Assert.Equal(settings, File.ReadAllText(_targets.MachineSettingsPath));
        Assert.Equal(trust, _targets.ClientDirectory.Folder(Host).ReadTrust());
        Assert.Equal(1, InScratchStore(server.Thumbprint));
        Assert.Single(ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath));
        Assert.Contains(summary, line => line.Contains("was already there", StringComparison.Ordinal));
        Assert.Contains(summary, line => line.Contains("0 added, 1 already present", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_OfANameAddress_LeavesTheHostsFileAlone()
    {
        var config = Validate(Export(address: "gateway.corp"));

        var summary = Import(config);

        Assert.Equal(Encoding.ASCII.GetBytes(OriginalHosts), File.ReadAllBytes(_targets.HostsFile.Path));
        Assert.Contains(summary, line => line.StartsWith("Hosts file not changed", StringComparison.Ordinal));
        Assert.Equal("gateway.corp", Assert.Single(ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath)).Address);
    }

    [Fact]
    public void Import_WarnsAboutAnEarlierHostsLineThatWins()
    {
        File.AppendAllText(_targets.HostsFile.Path, $"10.7.7.7 {Host}\r\n");
        var config = Validate(Export());

        var summary = Import(config);

        Assert.Contains(summary, line => line.Contains("Warning: an earlier line not managed by Dev.Conduit", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_WithARemoteNameTakenByAnotherServer_ChangesNothing()
    {
        File.WriteAllText(_targets.MachineSettingsPath, """{ "DevConduit": { "Remotes": { "devconduit": "https://someone-else.example" } } }""");
        var settings = File.ReadAllText(_targets.MachineSettingsPath);
        var config = Validate(Export());

        Assert.Throws<InvalidOperationException>(() => Import(config));

        Assert.Equal(Encoding.ASCII.GetBytes(OriginalHosts), File.ReadAllBytes(_targets.HostsFile.Path));
        Assert.Equal(settings, File.ReadAllText(_targets.MachineSettingsPath));
        Assert.False(Directory.Exists(_targets.ClientDirectory.Root));
        Assert.Equal(0, InScratchStore(config.ServerCertificates.Single().Thumbprint));
    }

    // ---- Forget --------------------------------------------------------------------------

    [Fact]
    public void Forget_UndoesTheImportExactly()
    {
        var config = Validate(Export());
        Import(config);
        Import(config);
        var server = config.ServerCertificates.Single();

        var removal = Forget();

        Assert.True(removal.Found);
        Assert.Equal("devconduit", removal.Remote);
        Assert.Equal(["api", "web"], removal.RemovedRoutes);

        Assert.Equal(Encoding.ASCII.GetBytes(OriginalHosts), File.ReadAllBytes(_targets.HostsFile.Path));
        Assert.False(Directory.Exists(_targets.ClientDirectory.Folder(Host).Directory));
        Assert.Equal(0, InScratchStore(server.Thumbprint));
        Assert.Empty(new RootStoreTrust(_targets.ClientDirectory.RootStoreRecordPath, ScratchStore, ScratchLocation).AddedFor(Host));
        Assert.Empty(ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath));

        // The settings are back as they were, apart from the AzureAd ids the import filled in.
        var expected = SettingsJson.Parse(OriginalSettings);
        expected["AzureAd"]!["TenantId"] = Tenant;
        expected["AzureAd"]!["ClientId"] = Client;
        Assert.True(JsonNode.DeepEquals(expected, Settings()), Settings().ToJsonString());
    }

    [Fact]
    public void Forget_LeavesACertificateSomeoneElseTrustedAlone()
    {
        var config = Validate(Export());
        var server = config.ServerCertificates.Single();

        // Already in the store before the import, so the import does not record adding it.
        using (var store = new X509Store(ScratchStore, ScratchLocation))
        {
            store.Open(OpenFlags.ReadWrite);
            using var publicOnly = TestCertificates.PublicOnly(server);
            store.Add(publicOnly);
        }

        Import(config);
        Forget();

        Assert.Equal(1, InScratchStore(server.Thumbprint));
    }

    [Fact]
    public void Forget_OfAServerNeverImported_FindsNothingAndChangesNothing()
    {
        var settings = File.ReadAllText(_targets.MachineSettingsPath);

        var removal = Forget("never.devconduit.internal");

        Assert.False(removal.Found);
        Assert.Equal(Encoding.ASCII.GetBytes(OriginalHosts), File.ReadAllBytes(_targets.HostsFile.Path));
        Assert.Equal(settings, File.ReadAllText(_targets.MachineSettingsPath));
    }

    [Fact]
    public void Forget_RefusesANameThatIsNotAHost() =>
        Assert.Throws<ArgumentException>(() => Forget("10.0.0.5"));

    [Fact]
    public void Forget_LeavesAFolderItDidNotWrite()
    {
        Import(Validate(Export()));
        var stray = Path.Combine(_targets.ClientDirectory.Folder(Host).Directory, "someone-elses");
        Directory.CreateDirectory(stray);

        var removal = Forget();

        Assert.True(Directory.Exists(stray));
        Assert.False(File.Exists(_targets.ClientDirectory.Folder(Host).TrustPath));
        Assert.False(File.Exists(_targets.ClientDirectory.Folder(Host).IdentityPath));
        Assert.Contains(removal.Summary, line => line.Contains("was left", StringComparison.Ordinal));
    }

    [Fact]
    public void Forget_OfOneServer_LeavesAnotherImportAlone()
    {
        Import(Validate(Export()));
        var other = ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath).Single() with { Hostname = "second.devconduit.internal", Remote = "second" };
        ProxyConfigImportRecords.Write(
            _targets.ClientDirectory.ImportRecordPath,
            [.. ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath), other]);

        Forget();

        Assert.Equal("second.devconduit.internal", Assert.Single(ProxyConfigImportRecords.Read(_targets.ClientDirectory.ImportRecordPath)).Hostname);
    }
}
