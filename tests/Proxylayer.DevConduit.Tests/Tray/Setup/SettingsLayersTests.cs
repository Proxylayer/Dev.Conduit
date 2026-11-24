using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// The files the setup window layers, and what it sees of each target file. This is also the
/// window's own model of the precedence TryAddTo applies at launch: machine below user.
/// </summary>
public sealed class SettingsLayersTests
{
    private static string Probe(string value) => $$"""{ "Probe": { "Value": "{{value}}" } }""";

    [Fact]
    public void Layers_AreTheTwoAppSettingsFilesThenMachineThenUser()
    {
        using var layout = new SettingsLayout();
        var layers = layout.Layers();

        Assert.Equal(
            ["appsettings.json", "appsettings.Uat.json", "machine settings", "user settings"],
            layers.Layers.Select(layer => layer.Name));
        Assert.Equal(
            [layout.AppSettingsPath, layout.EnvironmentSettingsPath, layout.MachinePath, layout.UserPath],
            layers.Layers.Select(layer => layer.Path));
        Assert.Equal(SettingsLayout.EnvironmentName, layers.EnvironmentName);
        Assert.Same(layout.Files, layers.Files);
    }

    [Fact]
    public void LayerForAndBelow_PlaceTheMachineFileBelowTheUserFile()
    {
        using var layout = new SettingsLayout();
        var layers = layout.Layers();

        Assert.Equal(layout.MachinePath, layers.LayerFor(SettingsTarget.Machine).Path);
        Assert.Equal(layout.UserPath, layers.LayerFor(SettingsTarget.User).Path);
        Assert.Equal(["appsettings.json", "appsettings.Uat.json"], layers.Below(SettingsTarget.Machine).Select(layer => layer.Name));
        Assert.Equal(["appsettings.json", "appsettings.Uat.json", "machine settings"], layers.Below(SettingsTarget.User).Select(layer => layer.Name));
    }

    [Fact]
    public void Exists_FollowsTheFile()
    {
        using var layout = new SettingsLayout();
        var layer = layout.Layers().LayerFor(SettingsTarget.User);

        Assert.False(layer.Exists);
        layout.User("{}");
        Assert.True(layer.Exists);
    }

    [Theory]
    [InlineData("", "Uat")]
    [InlineData("  ", "Uat")]
    [InlineData("C:\\app", "")]
    public void Constructor_RefusesABlankContentRootOrEnvironment(string contentRoot, string environment)
    {
        using var layout = new SettingsLayout();

        Assert.ThrowsAny<ArgumentException>(() => new SettingsLayers(contentRoot, environment, layout.Files));
    }

    [Fact]
    public void View_ForTheUserFileHasEveryOtherFileBelowAndTheUserFileOnTop()
    {
        using var layout = new SettingsLayout();
        layout.AppSettings(Probe("appsettings")).EnvironmentSettings(Probe("environment")).Machine(Probe("machine")).User(Probe("user"));

        var view = layout.View(SettingsTarget.User);

        Assert.Equal(SettingsTarget.User, view.Target);
        Assert.Equal("machine", view.Below["Probe:Value"]);
        Assert.Equal("user", view.Override["Probe:Value"]);
        Assert.Equal("user", view.Effective["Probe:Value"]);
    }

    [Fact]
    public void View_ForTheMachineFileLeavesTheUserFileOut()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).Machine(Probe("machine")).User(Probe("user"));

        var view = layout.View(SettingsTarget.Machine);

        Assert.Equal("environment", view.Below["Probe:Value"]);
        Assert.Equal("machine", view.Effective["Probe:Value"]);
    }

    [Fact]
    public void View_EnvironmentFileBeatsAppSettings()
    {
        using var layout = new SettingsLayout();
        layout.AppSettings(Probe("appsettings")).EnvironmentSettings(Probe("environment"));

        Assert.Equal("environment", layout.View(SettingsTarget.Machine).Effective["Probe:Value"]);
    }

    [Fact]
    public void SourceOf_NamesTheHighestLayerBelowThatDefinesAKey()
    {
        using var layout = new SettingsLayout();
        layout
            .AppSettings("""{ "Probe": { "Value": "a", "OnlyBase": "b" } }""")
            .EnvironmentSettings(Probe("environment"))
            .Machine("""{ "Probe": { "OnlyMachine": "m" } }""")
            .User("""{ "Probe": { "Value": "user" } }""");

        var view = layout.View(SettingsTarget.User);

        Assert.Equal("appsettings.Uat.json", view.SourceOf("Probe:Value"));
        Assert.Equal("appsettings.json", view.SourceOf("Probe:OnlyBase"));
        Assert.Equal("machine settings", view.SourceOf("Probe:OnlyMachine"));
        Assert.Equal("machine settings", view.SourceOf("Probe"));
        Assert.Null(view.SourceOf("Probe:Nowhere"));
    }

    [Fact]
    public void SourceOf_NeverNamesTheTargetFileItself()
    {
        using var layout = new SettingsLayout();
        layout.User(Probe("user"));

        Assert.Null(layout.View(SettingsTarget.User).SourceOf("Probe:Value"));
    }

    [Fact]
    public void EffectiveWith_ShowsTheChangeWithoutTouchingTheDocumentOrTheFile()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).User(Probe("user"));
        var original = File.ReadAllText(layout.UserPath);
        var view = layout.View(SettingsTarget.User);

        var changed = view.EffectiveWith(new SettingsChangeSet().Set("Probe:Value", "edited"));
        var removed = view.EffectiveWith(new SettingsChangeSet().Remove("Probe:Value"));

        Assert.Equal("edited", changed["Probe:Value"]);
        Assert.Equal("environment", removed["Probe:Value"]);
        Assert.Equal("user", view.Effective["Probe:Value"]);
        Assert.Equal("user", view.Document["Probe"]!["Value"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(layout.UserPath));
    }

    [Fact]
    public void View_ToleratesAbsentAndBlankFiles()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings("  ").Machine("");

        var view = layout.View(SettingsTarget.User);

        Assert.Empty(view.Document);
        Assert.Empty(view.Effective.GetChildren());
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("[ 1 ]")]
    public void View_RefusesAMalformedLowerFileNamingIt(string text)
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(text);

        var exception = Assert.Throws<InvalidDataException>(() => layout.View(SettingsTarget.User));

        Assert.Contains(layout.EnvironmentSettingsPath, exception.Message);
    }

    [Fact]
    public void View_RefusesAMalformedTargetFileNamingIt()
    {
        using var layout = new SettingsLayout();
        layout.User("{ broken");

        var exception = Assert.Throws<InvalidDataException>(() => layout.View(SettingsTarget.User));

        Assert.Contains(layout.UserPath, exception.Message);
    }

    [Fact]
    public void View_RefusesATargetFileThatIsNotAnObjectNamingIt()
    {
        using var layout = new SettingsLayout();
        layout.User("[ 1 ]");

        var exception = Assert.Throws<InvalidDataException>(() => layout.View(SettingsTarget.User));

        Assert.Contains(layout.UserPath, exception.Message);
        Assert.Contains("not a JSON object", exception.Message);
    }
}
