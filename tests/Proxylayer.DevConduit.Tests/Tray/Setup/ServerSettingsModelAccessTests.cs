using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// The Server page's access settings: the delegated scope clients are handed, the hosts the
/// server may call, and the warnings shown while users or targets are left open to everyone.
/// </summary>
public sealed class ServerSettingsModelAccessTests
{
    private const string OtherGuid = "33333333-3333-3333-3333-333333333333";

    /// <summary>appsettings.json allowing users and a group, and no targets.</summary>
    private const string BaseSettings = """
        {
          "DevConduit": {
            "AllowedUserOids": [ "aaaaaaaa-0000-0000-0000-000000000001" ],
            "AllowedGroupIds": "bbbbbbbb-0000-0000-0000-000000000001"
          }
        }
        """;

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

    // ---- Scope -------------------------------------------------------------------------

    [Fact]
    public void Scope_IsWrittenAsAListUnderAzureAd()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.Scope = " DevProxy.Forward ";

        var changes = model.ToChanges();

        var scopes = Assert.IsType<JsonArray>(changes.Values[ManagedSettingKeys.Scopes]);
        Assert.Equal(["DevProxy.Forward"], scopes.Select(scope => (string?)scope));
        Assert.Empty(model.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("DevConduit.Forward")]
    [InlineData("devconduit.forward")]
    public void Scope_BlankOrTheDefaultWritesNothing(string scope)
    {
        using var layout = Layout();
        var model = Load(layout);
        model.Scope = scope;

        var changes = model.ToChanges();

        Assert.Contains(ManagedSettingKeys.Scopes, changes.Removed);
        Assert.False(changes.Values.ContainsKey(ManagedSettingKeys.Scopes));
    }

    [Fact]
    public void Scope_TheDefaultIsWrittenOverALowerFileThatNamesAnother()
    {
        using var layout = new SettingsLayout().AppSettings("""{ "AzureAd": { "Scopes": [ "DevProxy.Forward" ] } }""");
        var model = Load(layout);
        Assert.Equal("DevProxy.Forward", model.Inherited(ManagedSettingKeys.Scopes));
        model.Scope = "DevConduit.Forward";

        var scopes = Assert.IsType<JsonArray>(model.ToChanges().Values[ManagedSettingKeys.Scopes]);

        Assert.Equal(["DevConduit.Forward"], scopes.Select(scope => (string?)scope));
    }

    [Fact]
    public void Scope_IsLoadedFromTheMachineFile()
    {
        using var layout = Layout("""{ "AzureAd": { "Scopes": [ "DevProxy.Forward" ] } }""");

        var model = Load(layout);

        Assert.Equal("DevProxy.Forward", model.Scope);
        var scopes = Assert.IsType<JsonArray>(model.ToChanges().Values[ManagedSettingKeys.Scopes]);
        Assert.Equal(["DevProxy.Forward"], scopes.Select(scope => (string?)scope));
    }

    [Theory]
    [InlineData("api://11111111-1111-1111-1111-111111111111/DevProxy.Forward")]
    [InlineData("Dev Proxy")]
    public void Validate_ReportsAScopeThatIsNotAPlainName(string scope)
    {
        using var layout = Layout();
        var model = Load(layout);
        model.Scope = scope;

        Assert.Contains(model.Validate(), problem => problem.StartsWith("The scope ", StringComparison.Ordinal));
    }

    // ---- Allowed targets -----------------------------------------------------------------

    [Fact]
    public void AllowedTargets_AreWrittenOnePerEntry()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.AllowedTargets = "api.internal.example\r\n*.corp.example:8443\r\n10.0.0.0/16";

        var targets = Assert.IsType<JsonArray>(model.ToChanges().Values[ManagedSettingKeys.AllowedTargets]);

        Assert.Equal(["api.internal.example", "*.corp.example:8443", "10.0.0.0/16"], targets.Select(target => (string?)target));
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Validate_ReportsAnAllowedTargetThatCannotBeUsed()
    {
        using var layout = Layout();
        var model = Load(layout);
        model.AllowedTargets = "api.internal.example\nhttps://web.internal\n10.0.1.0/16";

        var problems = model.Validate();

        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.StartsWith("Allowed targets: ", problem, StringComparison.Ordinal));
    }

    // ---- Warnings ------------------------------------------------------------------------

    [Fact]
    public void Warnings_SayBothWhenNoUsersGroupsOrTargetsAreAllowed()
    {
        using var layout = new SettingsLayout();
        var model = Load(layout);

        Assert.Equal([ServerExposureWarnings.AnyUser, ServerExposureWarnings.AnyTarget], model.Warnings());
        Assert.Equal(
            "Every user in the tenant can use this server. Add allowed users or groups to restrict.",
            ServerExposureWarnings.AnyUser);
        Assert.Equal("Any reachable host is allowed. Add allowed targets to restrict.", ServerExposureWarnings.AnyTarget);
    }

    [Theory]
    [InlineData(OtherGuid, "")]
    [InlineData("", OtherGuid)]
    public void Warnings_SayNothingAboutUsersWhenAUserOrAGroupIsAllowed(string users, string groups)
    {
        using var layout = new SettingsLayout();
        var model = Load(layout);
        model.AllowedUserOids = users;
        model.AllowedGroupIds = groups;
        model.AllowedTargets = "api.internal.example";

        Assert.Empty(model.Warnings());
    }

    [Fact]
    public void Warnings_FollowWhatTheLayersBelowAllow()
    {
        // appsettings.json allows users and a group; nothing allows targets.
        using var layout = Layout();
        var model = Load(layout);

        Assert.Equal([ServerExposureWarnings.AnyTarget], model.Warnings());
    }

    [Fact]
    public void Warnings_CountBlankEntriesForNothing() =>
        Assert.Equal(
            [ServerExposureWarnings.AnyUser, ServerExposureWarnings.AnyTarget],
            ServerExposureWarnings.For(["", " "], [null], ["  "]));

    [Fact]
    public void Warnings_TakeAMalformedTargetAsARestriction() =>
        Assert.Equal([ServerExposureWarnings.AnyUser], ServerExposureWarnings.For([], [], ["https://not valid"]));
}
