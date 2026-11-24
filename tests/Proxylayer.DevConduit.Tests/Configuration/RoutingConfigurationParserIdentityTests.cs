using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>How a remote under <c>DevConduit:Remotes</c> takes its Entra identity from the shared <c>AzureAd</c> section.</summary>
public sealed class RoutingConfigurationParserIdentityTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string LocalTenantId = "44444444-4444-4444-4444-444444444444";
    private const string LocalClientId = "55555555-5555-5555-5555-555555555555";

    private static IConfigurationSection AzureAd(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>($"AzureAd:{value.Key}", value.Value)))
            .Build()
            .GetSection("AzureAd");

    private static IConfigurationSection ServerRegistration() =>
        AzureAd(("TenantId", TenantId), ("ClientId", ClientId));

    /// <summary>Parses one remote, "uat", with these settings on top of its endpoint.</summary>
    private static RemoteDefinition Remote(IConfiguration azureAd, params (string Key, string? Value)[] settings)
    {
        var devConduit = new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings
                    .Select(setting => new KeyValuePair<string, string?>($"DevConduit:Remotes:uat:{setting.Key}", setting.Value))
                    .Append(new KeyValuePair<string, string?>("DevConduit:Remotes:uat:Endpoint", "https://devconduit.corp:8091")))
            .Build()
            .GetSection("DevConduit");

        return Assert.Single(RoutingConfigurationParser.Parse(devConduit, azureAd).Remotes);
    }

    [Fact]
    public void Parse_InheritsTheTenantAndClientIdFromAzureAdWhenTheRemoteNamesNone()
    {
        var remote = Remote(ServerRegistration());

        Assert.Equal(TenantId, remote.Identity.TenantId);
        Assert.Equal(ClientId, remote.Identity.ClientId);
        Assert.True(remote.Identity.IsComplete);
        Assert.True(remote.InheritsTenantId);
        Assert.True(remote.InheritsClientId);
        Assert.Equal("inherits AzureAd", remote.DescribeIdentity());
    }

    [Fact]
    public void Parse_KeepsTheRemotesOwnTenantAndClientId()
    {
        var remote = Remote(ServerRegistration(), ("TenantId", LocalTenantId), ("ClientId", LocalClientId));

        Assert.Equal(LocalTenantId, remote.Identity.TenantId);
        Assert.Equal(LocalClientId, remote.Identity.ClientId);
        Assert.False(remote.InheritsTenantId);
        Assert.False(remote.InheritsClientId);
    }

    [Fact]
    public void Parse_DefaultsTheScopeToTheRemotesOwnClientIdNotAzureAds()
    {
        var remote = Remote(ServerRegistration(), ("ClientId", LocalClientId));

        Assert.Equal([$"api://{LocalClientId}/DevConduit.Forward"], remote.Scopes);
    }

    [Fact]
    public void Parse_InheritsEachIdIndependently()
    {
        var remote = Remote(ServerRegistration(), ("TenantId", LocalTenantId));

        Assert.Equal(LocalTenantId, remote.Identity.TenantId);
        Assert.Equal(ClientId, remote.Identity.ClientId);
        Assert.False(remote.InheritsTenantId);
        Assert.True(remote.InheritsClientId);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t ")]
    public void Parse_TreatsAWhitespaceIdOnTheRemoteAsMissing(string blank)
    {
        var remote = Remote(ServerRegistration(), ("TenantId", blank), ("ClientId", blank));

        Assert.Equal(TenantId, remote.Identity.TenantId);
        Assert.Equal(ClientId, remote.Identity.ClientId);
    }

    [Fact]
    public void Parse_TrimsTheInheritedAzureAdIds()
    {
        var remote = Remote(AzureAd(("TenantId", $"  {TenantId} "), ("ClientId", $"\t{ClientId}  ")));

        Assert.Equal(TenantId, remote.Identity.TenantId);
        Assert.Equal(ClientId, remote.Identity.ClientId);
    }

    [Fact]
    public void Parse_LeavesTheIdentityEmptyWhenAzureAdHasNone()
    {
        var remote = Remote(AzureAd());

        Assert.Equal(string.Empty, remote.Identity.TenantId);
        Assert.Equal(string.Empty, remote.Identity.ClientId);
        Assert.False(remote.Identity.IsComplete);
    }

    [Fact]
    public void Parse_LeavesWhitespaceIdsEmptyWhenAzureAdHasNone()
    {
        var remote = Remote(AzureAd(("TenantId", "  "), ("ClientId", "")), ("TenantId", "  "), ("ClientId", "  "));

        Assert.Equal(string.Empty, remote.Identity.TenantId);
        Assert.Equal(string.Empty, remote.Identity.ClientId);
        Assert.False(remote.Identity.IsComplete);
    }

    [Fact]
    public void Parse_LeavesTheIdentityIncompleteWithOnlyATenant()
    {
        var remote = Remote(AzureAd(("TenantId", TenantId)));

        Assert.Equal(TenantId, remote.Identity.TenantId);
        Assert.Equal(string.Empty, remote.Identity.ClientId);
        Assert.False(remote.Identity.IsComplete);
    }

    [Fact]
    public void Parse_CarriesPlaceholderIdsThatIsCompleteStillRejects()
    {
        var remote = Remote(AzureAd(("TenantId", "<YOUR-TENANT-ID>"), ("ClientId", "<YOUR-CLIENT-ID>")));

        Assert.Equal("<YOUR-TENANT-ID>", remote.Identity.TenantId);
        Assert.False(remote.Identity.IsComplete);
    }

    [Fact]
    public void RemoteIdentity_IsIncompleteWhenBothIdsAreEmpty() =>
        Assert.False(new RemoteIdentity(string.Empty, string.Empty).IsComplete);

    [Fact]
    public void RemoteIdentity_KeyIgnoresTheCaseOfEitherId()
    {
        var lower = new RemoteIdentity("abcdef00-1111-1111-1111-111111111111", "abcdef00-2222-2222-2222-222222222222");
        var upper = new RemoteIdentity("ABCDEF00-1111-1111-1111-111111111111", "ABCDEF00-2222-2222-2222-222222222222");

        Assert.Equal(lower.Key, upper.Key);
    }

    [Fact]
    public void RemoteIdentity_KeyTellsDifferentTenantsAndClientsApart()
    {
        var identity = new RemoteIdentity(TenantId, ClientId);

        Assert.NotEqual(identity.Key, new RemoteIdentity(LocalTenantId, ClientId).Key);
        Assert.NotEqual(identity.Key, new RemoteIdentity(TenantId, LocalClientId).Key);
    }

    [Fact]
    public void Parse_DefaultsTheScopeToTheInheritedClientIdsForwardScope()
    {
        var remote = Remote(ServerRegistration());

        Assert.Equal([$"api://{ClientId}/{RemoteDefinition.DefaultScopeName}"], remote.Scopes);
        Assert.Equal("DevConduit.Forward", RemoteDefinition.DefaultScopeName);
    }

    [Fact]
    public void Parse_KeepsConfiguredScopesOverTheDefault()
    {
        var remote = Remote(ServerRegistration(), ("Scopes:0", "api://custom/Other.Scope"));

        Assert.Equal(["api://custom/Other.Scope"], remote.Scopes);
    }

    [Fact]
    public void Parse_KeepsConfiguredScopesOverTheDefaultForAnExplicitClientId()
    {
        var remote = Remote(
            ServerRegistration(),
            ("ClientId", LocalClientId),
            ("Scopes:0", "api://custom/One"),
            ("Scopes:1", " api://custom/Two "));

        Assert.Equal(["api://custom/One", "api://custom/Two"], remote.Scopes);
    }

    [Fact]
    public void Parse_AcceptsASingleScopeWrittenAsAString()
    {
        var remote = Remote(ServerRegistration(), ("Scopes", "api://custom/Only"));

        Assert.Equal(["api://custom/Only"], remote.Scopes);
    }
}
