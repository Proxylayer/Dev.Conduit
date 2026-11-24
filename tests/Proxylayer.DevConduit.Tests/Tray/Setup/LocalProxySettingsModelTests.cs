using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>The workstation's local-mode settings as the setup window edits them, for the user file.</summary>
public sealed class LocalProxySettingsModelTests
{
    private const string BaseSettings = """
        { "DevConduit": { "Local": { "Port": 8100, "SharedSecret": "below-secret" } } }
        """;

    private static readonly string[] LocalKeys =
    [
        ManagedSettingKeys.LocalCaptureEnabled,
        ManagedSettingKeys.LocalForwardProxyEnabled,
        ManagedSettingKeys.LocalForwardProxyPort,
        ManagedSettingKeys.LocalPort
    ];

    private static SettingsLayout Layout(string? user = null)
    {
        var layout = new SettingsLayout().AppSettings(BaseSettings);
        if (user is not null)
        {
            layout.User(user);
        }

        return layout;
    }

    [Fact]
    public void Load_ShowsMergedPortsAndSwitches()
    {
        using var layout = Layout();

        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));

        Assert.Equal(8100, model.Port);
        Assert.True(model.ForwardProxyEnabled);
        Assert.Equal(8093, model.ForwardProxyPort);
        Assert.False(model.CaptureEnabled);
    }

    [Fact]
    public void Load_ShowsTheValuesThisFileSets()
    {
        using var layout = Layout("""{ "DevConduit": { "Local": { "Port": 9000, "CaptureEnabled": true, "SharedSecret": "mine" } } }""");

        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));

        Assert.Equal(9000, model.Port);
        Assert.True(model.CaptureEnabled);
    }

    [Fact]
    public void ToChanges_OfAnUnchangedModelSetsNothing()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);

        var changes = LocalProxySettingsModel.Load(view).ToChanges();

        Assert.Empty(changes.Values);
        Assert.Equal(LocalKeys, changes.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ToChanges_OfAnUnchangedModelLeavesTheFileAsItIs()
    {
        using var layout = Layout("""{ "DevConduit": { "Local": { "Port": 9000, "ForwardProxyEnabled": false, "SharedSecret": "mine" } } }""");
        var view = layout.View(SettingsTarget.User);

        var changes = LocalProxySettingsModel.Load(view).ToChanges();

        var document = (JsonObject)view.Document.DeepClone();
        changes.ApplyTo(document);
        Assert.True(JsonNode.DeepEquals(view.Document, document), document.ToJsonString());
    }

    [Fact]
    public void ToChanges_WritesOnlyTheValuesThatDifferFromBelow()
    {
        using var layout = Layout();
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));

        model.Port = 9000;
        model.CaptureEnabled = true;

        var changes = model.ToChanges();

        Assert.Equal([ManagedSettingKeys.LocalCaptureEnabled, ManagedSettingKeys.LocalPort], changes.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(9000, changes.Values[ManagedSettingKeys.LocalPort].GetValue<int>());
        Assert.True(changes.Values[ManagedSettingKeys.LocalCaptureEnabled].GetValue<bool>());
    }

    [Fact]
    public void ToChanges_RemovesAValueSetBackToTheInheritedOne()
    {
        using var layout = Layout("""{ "DevConduit": { "Local": { "Port": 9000 } } }""");
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));

        model.Port = 8100;

        Assert.Contains(ManagedSettingKeys.LocalPort, model.ToChanges().Removed);
    }

    [Fact]
    public void ToChanges_NeverTouchesTheSharedSecret()
    {
        using var layout = Layout("""{ "DevConduit": { "Local": { "SharedSecret": "mine" } } }""");
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));
        model.Port = 9000;

        var changes = model.ToChanges();

        Assert.DoesNotContain("DevConduit:Local:SharedSecret", changes.Keys);
    }

    [Fact]
    public void ToChanges_TouchesOnlyUserKeys()
    {
        using var layout = Layout();
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));
        model.Port = 1;
        model.ForwardProxyEnabled = false;
        model.ForwardProxyPort = 2;
        model.CaptureEnabled = true;

        var changes = model.ToChanges();

        Assert.Equal(LocalKeys, changes.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.User));
    }

    [Fact]
    public void Validate_IsEmptyForUsableSettings()
    {
        using var layout = Layout();

        Assert.Empty(LocalProxySettingsModel.Load(layout.View(SettingsTarget.User)).Validate());
    }

    [Fact]
    public void Validate_ReportsWhatLocalModeWouldRefuse()
    {
        using var layout = Layout();
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));

        model.ForwardProxyPort = model.Port;

        Assert.Contains(model.Validate(), problem => problem.StartsWith("DevConduit:Local:ForwardProxyPort and DevConduit:Local:Port are both 8100.", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReportsAPortOutOfRange()
    {
        using var layout = Layout();
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));

        model.Port = 70000;

        Assert.Contains("DevConduit:Local:Port (70000) is not a usable TCP port.", model.Validate());
    }
}
