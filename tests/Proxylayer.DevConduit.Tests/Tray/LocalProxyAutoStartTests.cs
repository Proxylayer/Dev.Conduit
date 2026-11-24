using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Tests.Tray;

/// <summary>
/// When the tray starts the local proxy without a click: the switch is on, a server is
/// configured, local mode would start, and a server has Entra ids to sign in with. Pure: every
/// configuration is in memory.
/// </summary>
public sealed class LocalProxyAutoStartTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";

    /// <summary>A configuration from these values; a later value for the same key wins, as a higher file would.</summary>
    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            merged[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(merged).Build();
    }

    /// <summary>A workstation after one import: AzureAd filled from the file, one remote, one route.</summary>
    private static (string Key, string? Value)[] Imported(params (string Key, string? Value)[] extra) =>
    [
        ("AzureAd:TenantId", Tenant),
        ("AzureAd:ClientId", Client),
        ("DevConduit:Remotes:devconduit:Endpoint", "https://devconduit.example.internal:8091"),
        ("DevConduit:Routes:api:Target", "https://api.internal:8443"),
        ("DevConduit:Routes:api:Remote", "devconduit"),
        .. extra
    ];

    [Fact]
    public void Decide_StartsAWorkstationWithAnImportedServer()
    {
        var decision = LocalProxyAutoStart.Decide(Config(Imported()));

        Assert.True(decision.Start, decision.Reason);
        Assert.Equal("1 server configured.", decision.Reason);
    }

    [Fact]
    public void Decide_DoesNotStartWithNoServer()
    {
        var decision = LocalProxyAutoStart.Decide(Config(("AzureAd:TenantId", Tenant), ("AzureAd:ClientId", Client)));

        Assert.False(decision.Start);
        Assert.Equal("No server is imported yet.", decision.Reason);
    }

    [Fact]
    public void Decide_DoesNotStartWhenEveryRemoteIsSwitchedOff()
    {
        var decision = LocalProxyAutoStart.Decide(Config(Imported(("DevConduit:Remotes:devconduit:Enabled", "false"))));

        Assert.False(decision.Start);
    }

    [Theory]
    [InlineData("DevConduit:Local:BindAddress", "0.0.0.0")]
    [InlineData("DevConduit:Routes:api:Remote", "nowhere")]
    [InlineData("DevConduit:Remotes:devconduit:Endpoint", "not a uri")]
    public void Decide_DoesNotStartWithAFatalLocalModeProblem(string key, string value)
    {
        var decision = LocalProxyAutoStart.Decide(Config(Imported((key, value))));

        Assert.False(decision.Start);
        Assert.StartsWith("The configuration has a problem: ", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(Tenant, "")]
    [InlineData("", Client)]
    [InlineData("<YOUR-TENANT-ID>", Client)]
    [InlineData(Tenant, "YOUR-CLIENT-ID")]
    public void Decide_DoesNotStartWithoutUsableEntraIds(string tenant, string client)
    {
        var decision = LocalProxyAutoStart.Decide(Config(Imported(("AzureAd:TenantId", tenant), ("AzureAd:ClientId", client))));

        Assert.False(decision.Start);
        Assert.Equal("No server has an Entra tenant and client id to sign in with.", decision.Reason);
    }

    [Fact]
    public void Decide_StartsWhenOneOfSeveralRemotesHasItsOwnEntraIds()
    {
        var decision = LocalProxyAutoStart.Decide(Config(
            ("DevConduit:Remotes:uat", "https://uat.devconduit.internal:8091"),
            ("DevConduit:Remotes:prod:Endpoint", "https://prod.devconduit.internal:8091"),
            ("DevConduit:Remotes:prod:TenantId", Tenant),
            ("DevConduit:Remotes:prod:ClientId", Client),
            ("DevConduit:DefaultRemote", "prod")));

        Assert.True(decision.Start, decision.Reason);
        Assert.Equal("2 servers configured.", decision.Reason);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("nonsense", true)]
    [InlineData("false", false)]
    [InlineData(" FALSE ", false)]
    public void IsEnabled_IsOnUnlessSwitchedOff(string? value, bool enabled) =>
        Assert.Equal(enabled, LocalProxyAutoStart.IsEnabled(Config(("DevConduit:Tray:AutoStart", value))));

    [Fact]
    public void Decide_DoesNotStartWhenSwitchedOff()
    {
        var decision = LocalProxyAutoStart.Decide(Config(Imported(("DevConduit:Tray:AutoStart", "false"))));

        Assert.False(decision.Start);
        Assert.Equal("Start proxy automatically is off.", decision.Reason);
    }

    [Fact]
    public void Key_IsAManagedUserSetting() =>
        Assert.Contains(LocalProxyAutoStart.Key, Proxylayer.DevConduit.Configuration.ManagedSettingKeys.User);
}
