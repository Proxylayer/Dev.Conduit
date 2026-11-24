using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// The server's settings as the setup window edits them, for the machine file. Text and list
/// fields hold only what the file sets, so a blank one means inherit. Nothing about
/// certificates is a setting: the server manages its own.
/// </summary>
public sealed class ServerSettingsModelTests
{
    private const string ClientId = "11111111-1111-1111-1111-111111111111";
    private const string IdentityId = "22222222-2222-2222-2222-222222222222";
    private const string OtherGuid = "33333333-3333-3333-3333-333333333333";

    private const string BaseSettings = $$"""
        {
          "AzureAd": {
            "TenantId": "contoso.onmicrosoft.com",
            "ClientId": "{{ClientId}}",
            "ClientCredentials": [ { "SourceType": "SignedAssertionFromManagedIdentity", "ManagedIdentityClientId": "{{IdentityId}}" } ]
          },
          "DevConduit": {
            "Server": { "Hostname": "devconduit.example.internal", "ClientAddress": "10.20.30.40" },
            "AllowedUserOids": [ "aaaaaaaa-0000-0000-0000-000000000001", "aaaaaaaa-0000-0000-0000-000000000002", "aaaaaaaa-0000-0000-0000-000000000003" ],
            "AllowedGroupIds": "bbbbbbbb-0000-0000-0000-000000000001"
          }
        }
        """;

    private static readonly string[] ServerKeys =
    [
        ManagedSettingKeys.ClientCredentials,
        ManagedSettingKeys.ClientId,
        ManagedSettingKeys.Scopes,
        ManagedSettingKeys.TenantId,
        ManagedSettingKeys.AllowedGroupIds,
        ManagedSettingKeys.AllowedUserOids,
        ManagedSettingKeys.AllowedTargets,
        ManagedSettingKeys.ServerClientAddress,
        ManagedSettingKeys.ServerHostname,
        ManagedSettingKeys.ServerPort
    ];

    private static SettingsLayout Layout(string? machine = null)
    {
        var layout = new SettingsLayout().AppSettings(BaseSettings);
        if (machine is not null)
        {
            layout.Machine(machine);
        }

        return layout;
    }

    private static ServerSettingsModel Load(SettingsLayout layout) =>
        ServerSettingsModel.Load(layout.View(SettingsTarget.Machine));

    // ---- Load ----------------------------------------------------------------------------

    [Fact]
    public void Load_LeavesTextAndListFieldsBlankWhenTheFileSetsNothing()
    {
        using var layout = Layout();

        var model = Load(layout);

        Assert.Equal(string.Empty, model.Hostname);
        Assert.Equal(8091, model.Port);
        Assert.Equal(string.Empty, model.ClientAddress);
        Assert.Equal(string.Empty, model.TenantId);
        Assert.Equal(string.Empty, model.ClientId);
        Assert.Equal(string.Empty, model.ManagedIdentityClientId);
        Assert.Equal(string.Empty, model.AllowedUserOids);
        Assert.Equal(string.Empty, model.AllowedGroupIds);
    }

    [Fact]
    public void Inherited_ShowsWhatTheLayersBelowGiveABlankField()
    {
        using var layout = Layout();

        var model = Load(layout);

        Assert.Equal("devconduit.example.internal", model.Inherited(ManagedSettingKeys.ServerHostname));
        Assert.Equal("10.20.30.40", model.Inherited(ManagedSettingKeys.ServerClientAddress));
        Assert.Equal("contoso.onmicrosoft.com", model.Inherited(ManagedSettingKeys.TenantId));
        Assert.Equal(ClientId, model.Inherited(ManagedSettingKeys.ClientId));
        Assert.Equal(IdentityId, model.Inherited(ManagedSettingKeys.ClientCredentials));
        Assert.Equal(
            "aaaaaaaa-0000-0000-0000-000000000001, aaaaaaaa-0000-0000-0000-000000000002, aaaaaaaa-0000-0000-0000-000000000003",
            model.Inherited(ManagedSettingKeys.AllowedUserOids));
        Assert.Equal("bbbbbbbb-0000-0000-0000-000000000001", model.Inherited(ManagedSettingKeys.AllowedGroupIds));
        Assert.Equal("appsettings.json", model.InheritedFrom(ManagedSettingKeys.TenantId));
    }

    [Fact]
    public void Inherited_IsNullWhenNothingBelowSetsTheField()
    {
        using var layout = new SettingsLayout();

        var model = Load(layout);

        Assert.Null(model.Inherited(ManagedSettingKeys.TenantId));
        Assert.Null(model.Inherited(ManagedSettingKeys.ServerHostname));
        Assert.Null(model.Inherited(ManagedSettingKeys.ClientCredentials));
        Assert.Null(model.Inherited(ManagedSettingKeys.AllowedUserOids));
        Assert.Null(model.InheritedFrom(ManagedSettingKeys.TenantId));
    }

    [Fact]
    public void Load_ShowsWhatTheMachineFileSets()
    {
        using var layout = Layout($$"""
            {
              "AzureAd": { "TenantId": "fabrikam.onmicrosoft.com", "ClientCredentials": [ { "SourceType": "SignedAssertionFromManagedIdentity", "ManagedIdentityClientId": "{{OtherGuid}}" } ] },
              "DevConduit": { "Server": { "Hostname": "devconduit-prod.example.internal", "Port": 9443 }, "AllowedUserOids": [ "{{OtherGuid}}", "" ] }
            }
            """);

        var model = Load(layout);

        Assert.Equal("devconduit-prod.example.internal", model.Hostname);
        Assert.Equal(9443, model.Port);
        Assert.Equal("fabrikam.onmicrosoft.com", model.TenantId);
        Assert.Equal(OtherGuid, model.ManagedIdentityClientId);
        Assert.Equal(OtherGuid, model.AllowedUserOids);
        Assert.Equal("devconduit-prod.example.internal", model.EffectiveServer().NormalizedHostname);
    }

    // ---- Blank means inherit -------------------------------------------------------------

    [Fact]
    public void ToChanges_OfAnUnchangedModelSetsNothing()
    {
        using var layout = Layout();

        var changes = Load(layout).ToChanges();

        Assert.Empty(changes.Values);
        Assert.Equal(ServerKeys.Order(StringComparer.Ordinal), changes.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ToChanges_TreatsBlankFieldsAsInherit()
    {
        using var layout = Layout("""{ "AzureAd": { "TenantId": "fabrikam.onmicrosoft.com" }, "DevConduit": { "Server": { "Hostname": "other.devconduit.internal" } } }""");
        var view = layout.View(SettingsTarget.Machine);
        var model = ServerSettingsModel.Load(view);

        model.Hostname = " ";
        model.ClientAddress = "";
        model.TenantId = "   ";
        model.ClientId = "";
        model.ManagedIdentityClientId = " ";
        model.AllowedUserOids = "\r\n";
        model.AllowedGroupIds = " , ";

        var changes = model.ToChanges();
        Assert.Empty(changes.Values);

        var effective = view.EffectiveWith(changes);
        Assert.Equal("contoso.onmicrosoft.com", effective[ManagedSettingKeys.TenantId]);
        Assert.Equal("devconduit.example.internal", effective[ManagedSettingKeys.ServerHostname]);
    }

    [Fact]
    public void ToChanges_OfAnUnchangedModelLeavesTheFileAsItIs()
    {
        using var layout = Layout("""
            {
              "AzureAd": { "TenantId": "fabrikam.onmicrosoft.com" },
              "DevConduit": { "Server": { "Hostname": "other.devconduit.internal", "Port": 9443 }, "Route": { "AliasesOnly": true } },
              "Kestrel": { "Endpoints": { "Https": { "Url": "https://0.0.0.0:9443" } } }
            }
            """);
        var view = layout.View(SettingsTarget.Machine);

        var changes = ServerSettingsModel.Load(view).ToChanges();

        var document = (JsonObject)view.Document.DeepClone();
        changes.ApplyTo(document);
        Assert.True(JsonNode.DeepEquals(view.Document, document), document.ToJsonString());
    }

    // ---- Values that differ --------------------------------------------------------------

    [Theory]
    [InlineData("contoso.onmicrosoft.com", false)]
    [InlineData(" contoso.onmicrosoft.com ", false)]
    [InlineData("fabrikam.onmicrosoft.com", true)]
    public void ToChanges_WritesTextOnlyWhenItDiffersFromBelow(string tenant, bool written)
    {
        using var layout = Layout();
        var model = Load(layout);

        model.TenantId = tenant;

        Assert.Equal(written, model.ToChanges().Values.ContainsKey(ManagedSettingKeys.TenantId));
    }

    [Fact]
    public void ToChanges_WritesTheHostNameAsCertificatesUseIt()
    {
        using var layout = new SettingsLayout();
        var model = Load(layout);

        model.Hostname = " DevConduit.Example.Internal. ";

        Assert.Equal("devconduit.example.internal", model.ToChanges().Values[ManagedSettingKeys.ServerHostname].GetValue<string>());
    }

    [Fact]
    public void ToChanges_WritesThePortOnlyWhenItDiffersFromBelow()
    {
        using var layout = Layout();
        var model = Load(layout);

        model.Port = 8091;
        Assert.Contains(ManagedSettingKeys.ServerPort, model.ToChanges().Removed);

        model.Port = 9443;
        Assert.Equal(9443, model.ToChanges().Values[ManagedSettingKeys.ServerPort].GetValue<int>());
    }

    [Fact]
    public void ToChanges_WritesAManagedIdentityAsTheFirstClientCredential()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.Machine);
        var model = ServerSettingsModel.Load(view);

        model.ManagedIdentityClientId = $" {OtherGuid} ";

        var changes = model.ToChanges();
        var expected = new JsonArray(new JsonObject
        {
            ["SourceType"] = EntraCredential.ManagedIdentitySourceType,
            ["ManagedIdentityClientId"] = OtherGuid
        });
        Assert.True(JsonNode.DeepEquals(expected, changes.Values[ManagedSettingKeys.ClientCredentials]));

        var credential = EntraCredential.Describe(view.EffectiveWith(changes).GetSection("AzureAd"));
        Assert.Equal(EntraCredentialKind.ManagedIdentity, credential.Kind);
        Assert.Equal(OtherGuid, credential.ManagedIdentityClientId);
    }

    [Fact]
    public void ToChanges_RemovesAManagedIdentityEqualToTheInheritedOneIgnoringCase()
    {
        using var layout = Layout();
        var model = Load(layout);

        model.ManagedIdentityClientId = IdentityId.ToUpperInvariant();

        Assert.Contains(ManagedSettingKeys.ClientCredentials, model.ToChanges().Removed);
    }

    [Fact]
    public void ToChanges_PadsAShorterListOverALongerInheritedOne()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.Machine);
        var model = ServerSettingsModel.Load(view);

        model.AllowedUserOids = OtherGuid;

        var changes = model.ToChanges();
        Assert.True(JsonNode.DeepEquals(new JsonArray(OtherGuid, "", ""), changes.Values[ManagedSettingKeys.AllowedUserOids]));
        Assert.Equal([OtherGuid], SettingValues.ReadList(view.EffectiveWith(changes).GetSection(ManagedSettingKeys.AllowedUserOids)));
    }

    [Fact]
    public void ToChanges_WritesOneValueOverAnInheritedSingleStringAsAString()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.Machine);
        var model = ServerSettingsModel.Load(view);

        model.AllowedGroupIds = OtherGuid;

        var changes = model.ToChanges();
        Assert.Equal(OtherGuid, changes.Values[ManagedSettingKeys.AllowedGroupIds].GetValue<string>());
        Assert.Equal([OtherGuid], SettingValues.ReadList(view.EffectiveWith(changes).GetSection(ManagedSettingKeys.AllowedGroupIds)));
    }

    [Fact]
    public void ToChanges_RemovesAListEqualToTheInheritedOne()
    {
        using var layout = Layout();
        var model = Load(layout);

        model.AllowedUserOids = "aaaaaaaa-0000-0000-0000-000000000001\r\naaaaaaaa-0000-0000-0000-000000000002, AAAAAAAA-0000-0000-0000-000000000003";

        Assert.Contains(ManagedSettingKeys.AllowedUserOids, model.ToChanges().Removed);
    }

    [Fact]
    public void ToChanges_TouchesOnlyMachineKeys()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.Hostname = "other.devconduit.internal";
        model.Port = 9443;
        model.ClientAddress = "10.0.0.1";
        model.TenantId = "fabrikam.onmicrosoft.com";
        model.ClientId = OtherGuid;
        model.ManagedIdentityClientId = OtherGuid;
        model.AllowedUserOids = OtherGuid;
        model.AllowedGroupIds = OtherGuid;
        model.Scope = "DevProxy.Forward";
        model.AllowedTargets = "*.internal.example";

        var changes = model.ToChanges();

        Assert.Equal(ServerKeys.Order(StringComparer.Ordinal), changes.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.Machine));
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void ToChanges_NeverWritesACertificateOrAnEndpoint()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.Hostname = "other.devconduit.internal";

        Assert.DoesNotContain(model.ToChanges().Keys, key => key.StartsWith("Kestrel", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Validate ------------------------------------------------------------------------

    [Fact]
    public void Validate_IsEmptyForTheInheritedSettings()
    {
        using var layout = Layout();

        Assert.Empty(Load(layout).Validate());
    }

    [Fact]
    public void Validate_ReportsIdsThatAreNotGuids()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.TenantId = "not a tenant!";
        model.ClientId = "client";
        model.ManagedIdentityClientId = "identity";
        model.AllowedUserOids = $"{OtherGuid}\r\nuser";
        model.AllowedGroupIds = "group";

        Assert.Equal(
            [
                "The tenant id 'not a tenant!' is neither a GUID nor a domain name.",
                "The client id 'client' is not a GUID.",
                "The managed identity client id 'identity' is not a GUID.",
                "The allowed user object id 'user' is not a GUID.",
                "The allowed group id 'group' is not a GUID."
            ],
            model.Validate());
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("44444444-4444-4444-4444-444444444444")]
    public void Validate_AcceptsATenantGuidOrDomain(string tenant)
    {
        using var layout = Layout();
        var model = Load(layout);
        model.TenantId = tenant;

        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Validate_ReportsAHostNamePortAndAddressThatCannotBeUsed()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.Hostname = "10.0.0.1";
        model.Port = 0;
        model.ClientAddress = "not an address!";

        Assert.Equal(
            [
                "The host name '10.0.0.1' is not a DNS host name, for example devconduit.example.internal.",
                "The port 0 is not a TCP port.",
                "The client address 'not an address!' is not an IP address or host name."
            ],
            model.Validate());
    }
}
