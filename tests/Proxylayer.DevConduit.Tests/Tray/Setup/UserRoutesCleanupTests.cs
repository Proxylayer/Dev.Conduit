using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>The user-file routes left pointing at a server once it is removed, and the change that drops them. Pure.</summary>
public sealed class UserRoutesCleanupTests
{
    private static JsonObject User(string text) => SettingsJson.Parse(text);

    [Fact]
    public void AFileWithoutRoutes_NeedsNoChange()
    {
        Assert.Null(UserRoutesCleanup.For(User("{}"), "uat", ["api"]));
        Assert.Null(UserRoutesCleanup.For(User("""{ "DevConduit": { "Local": { "Port": 1 } } }"""), "uat", ["api"]));
    }

    [Fact]
    public void RoutesNotPointingAtTheServer_NeedNoChange() =>
        Assert.Null(UserRoutesCleanup.For(
            User("""{ "DevConduit": { "Routes": { "mine": "https://mine.internal", "x": { "Target": "https://x", "Remote": "other" } } } }"""),
            "uat",
            ["api"]));

    [Fact]
    public void RoutesNamingTheRemoteOrRemovedWithIt_AreDropped()
    {
        var change = UserRoutesCleanup.For(
            User("""
                { "DevConduit": { "Routes": {
                    "byRemote": { "Target": "https://a", "Remote": " UAT " },
                    "API": { "Enabled": false },
                    "mine": "https://mine.internal",
                    "other": { "Target": "https://o", "Remote": "other" } } } }
                """),
            "uat",
            ["api"]);

        Assert.NotNull(change);
        Assert.Equal([ManagedSettingKeys.Routes], change.Keys);
        var routes = change.Values[ManagedSettingKeys.Routes].AsObject();
        Assert.Equal(["mine", "other"], routes.Select(route => route.Key));
    }

    [Fact]
    public void WhenEveryRouteGoes_TheSectionIsRemoved()
    {
        var change = UserRoutesCleanup.For(User("""{ "devconduit": { "routes": { "api": "https://a" } } }"""), "uat", ["api"]);

        Assert.NotNull(change);
        Assert.Equal([ManagedSettingKeys.Routes], change.Removed);
        Assert.Empty(change.Values);
    }

    [Fact]
    public void ABlankRemote_MatchesOnlyTheRemovedRouteNames()
    {
        var change = UserRoutesCleanup.For(
            User("""{ "DevConduit": { "Routes": { "a": { "Target": "https://a", "Remote": "" }, "api": "https://api" } } }"""),
            string.Empty,
            ["api"]);

        Assert.Equal(["a"], change!.Values[ManagedSettingKeys.Routes].AsObject().Select(route => route.Key));
    }

    [Fact]
    public void TheDocumentGiven_IsNotChanged()
    {
        var user = User("""{ "DevConduit": { "Routes": { "api": "https://a", "keep": "https://k" } } }""");
        var before = user.ToJsonString();

        UserRoutesCleanup.For(user, "uat", ["api"]);

        Assert.Equal(before, user.ToJsonString());
    }

    [Fact]
    public void TheChange_IsWithinTheUserAllowList()
    {
        var change = UserRoutesCleanup.For(User("""{ "DevConduit": { "Routes": { "api": "https://a", "keep": "https://k" } } }"""), "uat", ["api"]);

        Assert.Empty(ManagedSettingKeys.Unmanaged(change!, ManagedSettingKeys.User));
    }
}
