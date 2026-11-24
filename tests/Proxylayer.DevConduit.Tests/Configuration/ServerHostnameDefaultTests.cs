using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// The server's host name when none is configured: this computer's DNS name, for a server run as
/// or beside the service, by the rule the data root follows; never over an explicit host name or
/// Kestrel:Endpoints. The service probe and the computer's name are injected.
/// </summary>
public sealed class ServerHostnameDefaultTests
{
    private const string ComputerName = "workstation01";

    private static ConfigurationManager Configuration(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return configuration;
    }

    // ---- FromNames and ComputerName --------------------------------------------------------

    [Theory]
    [InlineData("WORKSTATION01", "corp.example.com", "WORKSTATION01", "workstation01.corp.example.com")]
    [InlineData("WORKSTATION01", "", "WORKSTATION01", "workstation01")]
    [InlineData("workstation01.corp.example.com", "corp.example.com", "WORKSTATION01", "workstation01.corp.example.com")]
    [InlineData("workstation01", "bad_suffix", "WORKSTATION01", "workstation01")]
    [InlineData(null, null, "WORKSTATION01", "workstation01")]
    [InlineData("", "", "WORK.STATION", "work.station")]
    public void FromNames_PrefersTheFullyQualifiedNameThenTheHostNameThenTheComputerName(string? host, string? domain, string machine, string expected) =>
        Assert.Equal(expected, ServerHostnameDefault.FromNames(host, domain, machine));

    [Fact]
    public void FromNames_IsNullWhenNoNameIsADnsHostName() =>
        Assert.Null(ServerHostnameDefault.FromNames("bad_name", "", "BAD_NAME"));

    [Fact]
    public void ComputerName_IsThisComputersNameInLowerCase()
    {
        var name = ServerHostnameDefault.ComputerName();

        Assert.NotNull(name);
        Assert.True(DnsHostname.IsValid(name));
        Assert.Equal(name.ToLowerInvariant(), name);
        Assert.StartsWith(Environment.MachineName.ToLowerInvariant(), name, StringComparison.Ordinal);
    }

    // ---- UsesComputerName and For ----------------------------------------------------------

    [Theory]
    [InlineData(DevConduitMode.Server, true, false, true)]
    [InlineData(DevConduitMode.Server, false, true, true)]
    [InlineData(DevConduitMode.Server, false, false, false)]
    [InlineData(DevConduitMode.Local, false, true, false)]
    [InlineData(DevConduitMode.Local, true, true, false)]
    public void UsesComputerName_FollowsTheDataRootRule(DevConduitMode mode, bool asService, bool installed, bool expected)
    {
        Assert.Equal(expected, ServerHostnameDefault.UsesComputerName(mode, asService, installed));
        Assert.Equal(DataRootDefault.UsesMachineFolder(mode, asService, installed), ServerHostnameDefault.UsesComputerName(mode, asService, installed));
    }

    [Fact]
    public void For_IsTheComputerNameOnlyWhenNothingNamesAHostOrAnEndpoint()
    {
        Assert.Equal(ComputerName, ServerHostnameDefault.For(Configuration(), applies: true, () => "WORKSTATION01"));
        Assert.Null(ServerHostnameDefault.For(Configuration(), applies: false, () => ComputerName));
        Assert.Null(ServerHostnameDefault.For(Configuration((ServerHostnameDefault.Key, "vm.internal")), applies: true, () => ComputerName));
        Assert.Null(ServerHostnameDefault.For(Configuration(("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:8091")), applies: true, () => ComputerName));
        Assert.Null(ServerHostnameDefault.For(Configuration(), applies: true, () => null));
        Assert.Null(ServerHostnameDefault.For(Configuration(), applies: true, () => "not_a_name"));
    }

    // ---- Apply -----------------------------------------------------------------------------

    [Fact]
    public void Apply_UsesTheComputerNameForTheService()
    {
        var configuration = Configuration();
        var asked = false;

        var applied = ServerHostnameDefault.Apply(configuration, DevConduitMode.Server, runningAsService: true, () => asked = true, () => ComputerName);

        Assert.Equal(ComputerName, applied);
        Assert.Equal(ComputerName, configuration[ServerHostnameDefault.Key]);
        Assert.False(asked);
    }

    [Fact]
    public void Apply_UsesTheComputerNameForAConsoleRunBesideTheInstalledService()
    {
        var configuration = Configuration();

        Assert.Equal(ComputerName, ServerHostnameDefault.Apply(configuration, DevConduitMode.Server, runningAsService: false, () => true, () => ComputerName));
        Assert.Equal(ComputerName, configuration.GetSection(DevConduitOptions.SectionName).Get<DevConduitOptions>()!.Server.Hostname);
    }

    [Fact]
    public void Apply_LeavesAConsoleRunWithoutTheServiceAsItWas()
    {
        var configuration = Configuration();

        Assert.Null(ServerHostnameDefault.Apply(configuration, DevConduitMode.Server, runningAsService: false, () => false, () => ComputerName));
        Assert.Null(configuration[ServerHostnameDefault.Key]);
    }

    [Fact]
    public void Apply_NeverTouchesLocalModeAndDoesNotAskTheProbe()
    {
        var configuration = Configuration();
        var asked = false;

        Assert.Null(ServerHostnameDefault.Apply(configuration, DevConduitMode.Local, runningAsService: false, () => asked = true, () => ComputerName));
        Assert.False(asked);
    }

    [Fact]
    public void Apply_AnExplicitHostNameWins()
    {
        var configuration = Configuration((ServerHostnameDefault.Key, "devconduit.example.internal"));

        Assert.Null(ServerHostnameDefault.Apply(configuration, DevConduitMode.Server, runningAsService: true, () => true, () => ComputerName));
        Assert.Equal("devconduit.example.internal", configuration[ServerHostnameDefault.Key]);
    }

    [Fact]
    public void Apply_ExplicitKestrelEndpointsStillOverride()
    {
        var configuration = Configuration(("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:443"));

        Assert.Null(ServerHostnameDefault.Apply(configuration, DevConduitMode.Server, runningAsService: true, () => true, () => ComputerName));
        Assert.Null(configuration[ServerHostnameDefault.Key]);
    }

    [Fact]
    public void With_LayersTheHostNameOverTheConfiguration()
    {
        var configuration = Configuration(("DevConduit:Server:Port", "9443"));

        var with = ServerHostnameDefault.With(configuration, ComputerName);

        Assert.Equal(ComputerName, with[ServerHostnameDefault.Key]);
        Assert.Equal("9443", with["DevConduit:Server:Port"]);
        Assert.Same(configuration, ServerHostnameDefault.With(configuration, null));
    }

    // ---- The setup window and the export ----------------------------------------------------

    [Fact]
    public void ServerSettingsModel_ShowsTheComputerNameButDoesNotWriteIt()
    {
        using var layout = new SettingsLayout().Machine("""{ "DevConduit": { "Server": { "ClientAddress": "10.0.0.5" } } }""");

        var model = ServerSettingsModel.Load(layout.View(SettingsTarget.Machine), ComputerName);

        Assert.Equal(string.Empty, model.Hostname);
        Assert.Equal(ComputerName, model.DefaultHostname);
        Assert.Equal(ComputerName, model.EffectiveServer().Hostname);
        Assert.Contains(ManagedSettingKeys.ServerHostname, model.ToChanges().Removed);
        Assert.DoesNotContain(ManagedSettingKeys.ServerHostname, model.ToChanges().Values.Keys);
    }

    [Fact]
    public void ServerSettingsModel_WritesTheHostNameOnceItIsTyped()
    {
        using var layout = new SettingsLayout();
        var model = ServerSettingsModel.Load(layout.View(SettingsTarget.Machine), ComputerName);

        model.Hostname = "DEVCONDUIT.example.internal";

        Assert.Null(model.DefaultHostname);
        Assert.Equal("devconduit.example.internal", model.ToChanges().Values[ManagedSettingKeys.ServerHostname].GetValue<string>());
    }

    [Fact]
    public void ServerSettingsModel_HasNoDefaultWhenAFileNamesTheHostOrNoComputerNameIsGiven()
    {
        using var layout = new SettingsLayout().Machine("""{ "DevConduit": { "Server": { "Hostname": "vm.internal" } } }""");

        using var empty = new SettingsLayout();

        Assert.Null(ServerSettingsModel.Load(layout.View(SettingsTarget.Machine), ComputerName).DefaultHostname);
        Assert.Null(ServerSettingsModel.Load(empty.View(SettingsTarget.Machine)).DefaultHostname);
    }

    [Fact]
    public void ServerSettingsModel_ReloadKeepsTheComputerName()
    {
        using var layout = new SettingsLayout();
        var model = ServerSettingsModel.Load(layout.View(SettingsTarget.Machine), ComputerName);

        Assert.Equal(ComputerName, model.Reload(layout.View(SettingsTarget.Machine)).DefaultHostname);
    }

    [Fact]
    public void ClientConfigExportPlan_UsesTheEffectiveHostName()
    {
        var configuration = Configuration(("AzureAd:TenantId", "11111111-2222-3333-4444-555555555555"), ("AzureAd:ClientId", "66666666-7777-8888-9999-000000000000"));

        var plan = ClientConfigExportPlan.From(ServerHostnameDefault.With(configuration, ServerHostnameDefault.For(configuration, applies: true, () => ComputerName)));

        Assert.Equal(ComputerName, plan.Hostname);
        Assert.Equal("workstation01.conduit", plan.FileName);
        Assert.Null(plan.Problem);
    }

    /// <summary>
    /// A computer's name is often a single label that clients cannot resolve: the export still
    /// works, and the import maps the name to the client address in the hosts file.
    /// </summary>
    [Fact]
    public void ASingleLabelComputerName_ExportsAndImportsThroughTheHostsEntry()
    {
        using var root = new TempDirectory();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var authority = new ServerCertificateAuthority(ServerCertificateStore.ForDataRoot(Path.Combine(root.Path, "server")), clock, NullLogger.Instance);
        var export = new ProxyConfigExporter(authority, clock).Export(
            new ProxyConfigExportRequest(
                ComputerName, "10.20.30.40", 8091, "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000",
                [new("api", "https://api.internal")]),
            Path.Combine(root.Path, ProxyConfigDocument.FileNameFor(ComputerName)));

        Assert.True(ProxyConfigValidator.TryValidate(File.ReadAllBytes(export.Path), clock.GetUtcNow(), out var config, out var error), error);
        Assert.Equal(ComputerName, config.Hostname);
        Assert.True(config.AddressIsIp);
        Assert.All(config.ServerCertificates, certificate => Assert.Null(ServerCertificateRules.Problem(certificate, ComputerName)));

        var hostsPath = Path.Combine(root.Path, "hosts");
        File.WriteAllBytes(hostsPath, Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n"));
        var targets = new ProxyConfigImportTargets(
            new HostsFile(hostsPath),
            new ClientCertificateDirectory(Path.Combine(root.Path, "client"), restrictFolders: false),
            Path.Combine(root.Path, "machine", "settings.json"),
            StoreName.AddressBook,
            StoreLocation.CurrentUser,
            ProtectMachineSettings: false);
        try
        {
            new ProxyConfigImporter(targets, clock).Import(config);

            Assert.Contains(HostsFileText.ManagedLine("10.20.30.40", ComputerName), File.ReadAllText(hostsPath), StringComparison.Ordinal);
            Assert.Contains("https://workstation01:8091", File.ReadAllText(targets.MachineSettingsPath), StringComparison.Ordinal);
        }
        finally
        {
            new RootStoreTrust(targets.ClientDirectory.RootStoreRecordPath, StoreName.AddressBook, StoreLocation.CurrentUser).RemoveAll();
        }
    }
}
