using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>What "Export client configuration" starts from and checks before asking for elevation. Pure.</summary>
public sealed class ClientConfigExportPlanTests
{
    private const string Host = "devconduit.example.internal";

    private static IConfiguration Machine(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";

    /// <summary>A server that can export, with <paramref name="values"/> added or overriding.</summary>
    private static IConfiguration Ready(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DevConduit:Server:Hostname"] = Host,
            ["AzureAd:TenantId"] = Tenant,
            ["AzureAd:ClientId"] = Client
        };

        foreach (var (key, value) in values)
        {
            settings[key] = value;
        }

        return Machine([.. settings.Select(setting => (setting.Key, setting.Value))]);
    }

    [Fact]
    public void PathProblem_RefusesAFolderNamedLikeTheFile()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, $"{Host}.conduit");
        Directory.CreateDirectory(path);

        Assert.Equal("A folder has that name; choose a file name.", ClientConfigExportPlan.PathProblem(path));
    }

    [Fact]
    public void From_TakesTheNormalizedHostnameTheTrimmedAddressAndTheEntraIds()
    {
        var plan = ClientConfigExportPlan.From(Ready(
            ("DevConduit:Server:Hostname", " DevConduit.Example.Internal. "),
            ("DevConduit:Server:ClientAddress", " 10.0.0.5 ")));

        Assert.Equal(Host, plan.Hostname);
        Assert.Equal("10.0.0.5", plan.ClientAddress);
        Assert.Equal(Tenant, plan.TenantId);
        Assert.Equal(Client, plan.ClientId);
        Assert.Null(plan.Problem);
    }

    [Fact]
    public void FileName_IsTheHostWithTheConduitExtension() =>
        Assert.Equal($"{Host}.conduit", ClientConfigExportPlan.From(Ready()).FileName);

    [Fact]
    public void From_WithoutAnAddress_PrefillsNothing() =>
        Assert.Equal(string.Empty, ClientConfigExportPlan.From(Ready()).ClientAddress);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAHostname_TheProblemSaysToFillItInOnTheServerPage(string? hostname)
    {
        var plan = ClientConfigExportPlan.From(Ready(("DevConduit:Server:Hostname", hostname)));

        Assert.Equal(string.Empty, plan.Hostname);
        Assert.Equal("Fill in the host name on the Server page and save, then export again.", plan.Problem);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("bad_name")]
    public void WithAHostnameThatIsNotADnsName_TheProblemSaysSo(string hostname) =>
        Assert.Equal($"The host name '{hostname}' is not a DNS host name.", ClientConfigExportPlan.From(Ready(("DevConduit:Server:Hostname", hostname))).Problem);

    [Theory]
    [InlineData("AzureAd:TenantId", "", "the Entra tenant id")]
    [InlineData("AzureAd:TenantId", "<tenant-id>", "the Entra tenant id")]
    [InlineData("AzureAd:ClientId", null, "the Entra client id")]
    [InlineData("AzureAd:ClientId", "YOUR-CLIENT-ID", "the Entra client id")]
    public void WithoutAnEntraId_ThePlanRefusesBeforeTheElevationPrompt(string key, string? value, string missing)
    {
        var plan = ClientConfigExportPlan.From(Ready((key, value)));

        Assert.Equal($"Fill in {missing} on the Server page and save, then export again.", plan.Problem);
    }

    [Fact]
    public void WithNothingSet_TheProblemNamesEverythingMissing() =>
        Assert.Equal(
            "Fill in the host name, the Entra tenant id and the Entra client id on the Server page and save, then export again.",
            ClientConfigExportPlan.From(Machine()).Problem);

    [Fact]
    public void SecretNote_SaysTheFileHoldsAClientCertificate() =>
        Assert.Contains("client certificate", ClientConfigExportPlan.SecretNote);

    // ---- AddressProblem ------------------------------------------------------------------

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData(" 10.0.0.5 ")]
    [InlineData("fd00::5")]
    [InlineData("gateway.corp")]
    public void AddressProblem_IsNullForAnAddressOrName(string address) =>
        Assert.Null(ClientConfigExportPlan.AddressProblem(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AddressProblem_AsksForOneWhenBlank(string? address) =>
        Assert.Equal("Enter the IP address (or name) clients map the host name to.", ClientConfigExportPlan.AddressProblem(address));

    [Theory]
    [InlineData(" bad_name ", "bad_name")]
    [InlineData("http://10.0.0.5", "http://10.0.0.5")]
    public void AddressProblem_NamesAnUnusableAddress(string address, string shown) =>
        Assert.Equal($"'{shown}' is not an IP address or host name.", ClientConfigExportPlan.AddressProblem(address));

    // ---- PathProblem ---------------------------------------------------------------------

    [Fact]
    public void PathProblem_IsNullForAConduitFileInAnExistingFolder()
    {
        using var folder = new TempDirectory();

        Assert.Null(ClientConfigExportPlan.PathProblem(Path.Combine(folder.Path, $"{Host}.conduit")));
        Assert.Null(ClientConfigExportPlan.PathProblem(Path.Combine(folder.Path, "X.CONDUIT")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.conduit")]
    [InlineData(@"C:relative.conduit")]
    public void PathProblem_AsksWhereToSaveForABlankOrRelativePath(string? path) =>
        Assert.Equal("Choose where to save the file.", ClientConfigExportPlan.PathProblem(path));

    [Fact]
    public void PathProblem_NeedsTheExtension()
    {
        using var folder = new TempDirectory();

        Assert.Equal("The file name must end in .conduit.", ClientConfigExportPlan.PathProblem(Path.Combine(folder.Path, "host.json")));
        Assert.Equal("The file name must end in .conduit.", ClientConfigExportPlan.PathProblem(Path.Combine(folder.Path, "host.proxyconfig")));
    }

    [Fact]
    public void PathProblem_NeedsAnExistingFolder()
    {
        using var folder = new TempDirectory();

        Assert.Equal("The folder does not exist.", ClientConfigExportPlan.PathProblem(Path.Combine(folder.Path, "missing", "host.conduit")));
    }
}
