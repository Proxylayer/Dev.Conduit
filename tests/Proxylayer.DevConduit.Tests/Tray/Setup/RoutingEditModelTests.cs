using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// The routes as the setup window edits them: which rows reach the settings file, how an
/// inherited row is switched off, how a route naming no server goes to the only one, and that
/// what is written reads back through the real routing parser as intended.
/// </summary>
public sealed class RoutingEditModelTests
{
    private const string UatEndpoint = "https://devconduit-uat.corp:8091";
    private const string ProdEndpoint = "https://devconduit-prod.corp:8091";

    private const string BaseSettings = $$"""
        {
          "AzureAd": { "TenantId": "tenant-base", "ClientId": "client-base" },
          "DevConduit": {
            "Remotes": {
              "uat": "{{UatEndpoint}}",
              "prod": {
                "Endpoint": "{{ProdEndpoint}}",
                "TenantId": "tenant-prod",
                "Targets": [ "*.prod.internal", "10.50.1.10:8080" ]
              }
            },
            "Routes": {
              "api": "http://10.0.0.2:8080",
              "orders": { "Target": "http://10.50.1.10:8080", "Remote": "prod" }
            },
            "DefaultRemote": "uat"
          }
        }
        """;

    private static SettingsLayout Layout(string? user = null)
    {
        var layout = new SettingsLayout().AppSettings(BaseSettings);
        if (user is not null)
        {
            layout.User(user);
        }

        return layout;
    }

    private static RoutingConfiguration Parse(IConfiguration configuration) =>
        RoutingConfigurationParser.Parse(configuration.GetSection("DevConduit"), configuration.GetSection("AzureAd"));

    private static RouteRow Route(RoutingEditModel model, string name) =>
        model.Routes.Single(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));

    private static JsonObject Written(SettingsChangeSet changes, string key) =>
        Assert.IsType<JsonObject>(changes.Values[key]);

    private static JsonObject ApplyToCopy(SettingsView view, SettingsChangeSet changes)
    {
        var document = (JsonObject)view.Document.DeepClone();
        changes.ApplyTo(document);
        return document;
    }

    private static void AssertSameJson(JsonNode expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected {expected.ToJsonString()} but got {actual.ToJsonString()}");

    // ---- Load ----------------------------------------------------------------------------

    [Fact]
    public void Load_ShowsTheMergedRowsAsInheritedAndUnchanged()
    {
        using var layout = Layout();

        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        Assert.Equal(SettingsTarget.User, model.Target);
        Assert.Equal(["api", "orders"], model.Routes.Select(row => row.Name).Order(StringComparer.Ordinal));

        var orders = Route(model, "orders");
        Assert.Equal("http://10.50.1.10:8080", orders.Target);
        Assert.Equal("prod", orders.Remote);
        Assert.Equal("appsettings.json", orders.InheritedFrom);
        Assert.True(model.MatchesInherited(orders));
        Assert.Equal("http://10.0.0.2:8080", Route(model, "api").Target);

        Assert.Equal(["prod", "uat"], model.ServerNames.Order(StringComparer.Ordinal));
        Assert.Equal("uat", model.DefaultServer);
        Assert.Equal("uat", model.ServerOf(Route(model, "api")));
    }

    [Fact]
    public void Load_MarksRowsTheTargetFileAddsAsAddedHere()
    {
        using var layout = Layout("""{ "DevConduit": { "Routes": { "local": "http://localhost:5000" } } }""");

        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        var local = Route(model, "local");
        Assert.Null(local.InheritedFrom);
        Assert.Null(local.Inherited);
        Assert.False(model.MatchesInherited(local));
    }

    [Fact]
    public void Load_NamesTheLayerARowComesFrom()
    {
        using var layout = Layout();
        layout.Machine("""{ "DevConduit": { "Routes": { "records": { "Target": "http://10.0.0.9:9000", "Remote": "uat" } } } }""");

        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        Assert.Equal("machine settings", Route(model, "records").InheritedFrom);
        Assert.Equal("appsettings.json", Route(model, "api").InheritedFrom);
    }

    [Fact]
    public void Load_WithOneServerMakesItTheDefault()
    {
        using var layout = new SettingsLayout().Machine($$"""
            { "DevConduit": { "Remotes": { "uat": "{{UatEndpoint}}" }, "Routes": { "api": "http://10.0.0.2:8080" } } }
            """);

        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        Assert.Equal(["uat"], model.ServerNames);
        Assert.Equal("uat", model.DefaultServer);
        Assert.Equal("uat", model.ServerOf(Route(model, "api")));
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Load_WithSeveralServersAndNoDefaultHasNone()
    {
        using var layout = new SettingsLayout().Machine($$"""
            { "DevConduit": { "Remotes": { "uat": "{{UatEndpoint}}", "prod": "{{ProdEndpoint}}" }, "Routes": { "api": "http://10.0.0.2:8080" } } }
            """);

        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        Assert.Equal(string.Empty, model.DefaultServer);
        Assert.Contains(model.Validate(), problem => problem.Contains("api", StringComparison.Ordinal) && problem.Contains("no Remote", StringComparison.Ordinal));
    }

    // ---- Unchanged -----------------------------------------------------------------------

    [Fact]
    public void ToChanges_OfAnUnchangedModelOverAnEmptyFileSetsNothing()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        var changes = model.ToChanges();

        Assert.Empty(changes.Values);
        Assert.Equal([ManagedSettingKeys.Routes], changes.Removed);
        Assert.Empty(ApplyToCopy(view, changes));
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void ToChanges_OfAnUnchangedModelLeavesAFileOfAddedRowsAsItIs()
    {
        using var layout = Layout("""
            {
              "DevConduit": {
                "Remotes": { "dev": { "Endpoint": "https://dev.corp:8091", "Scopes": [ "api://dev/.default" ] } },
                "Routes": { "local": { "Target": "http://localhost:5000", "Remote": "dev" } },
                "DefaultRemote": "dev"
              },
              "Unrelated": { "Kept": true }
            }
            """);
        var view = layout.View(SettingsTarget.User);

        var changes = RoutingEditModel.Load(view).ToChanges();

        AssertSameJson(view.Document, ApplyToCopy(view, changes));
    }

    [Fact]
    public void ToChanges_OfAnUnchangedModelLeavesTheEffectiveConfigurationAsItIs()
    {
        using var layout = Layout("""
            {
              "DevConduit": {
                "Remotes": { "prod": { "Endpoint": "https://prod-alt.corp:8091" }, "dev": "https://dev.corp:8091" },
                "Routes": { "api": { "Enabled": false } },
                "DefaultRemote": "prod"
              }
            }
            """);
        var view = layout.View(SettingsTarget.User);

        var after = view.EffectiveWith(RoutingEditModel.Load(view).ToChanges());

        Assert.Equal(Describe(Parse(view.Effective)), Describe(Parse(after)));
    }

    /// <summary>Everything the parser makes of a routing configuration, as comparable text.</summary>
    private static string[] Describe(RoutingConfiguration routing) =>
    [
        .. routing.Remotes
            .OrderBy(remote => remote.Name, StringComparer.Ordinal)
            .Select(remote => $"remote {remote.Name} {remote.BaseAddress} {remote.Identity} "
                + $"{string.Join(',', remote.Scopes)} {string.Join(',', remote.Targets)}"),
        .. routing.Routes
            .OrderBy(route => route.Name, StringComparer.Ordinal)
            .Select(route => $"route {route.Name} {route.Target} {route.Remote}"),
        $"default {routing.DefaultRemote?.Name}",
        .. routing.Problems
    ];

    [Fact]
    public void ToChanges_KeepsAPropertyTheWindowDoesNotEditWhenAnotherIsChanged()
    {
        using var layout = Layout("""{ "DevConduit": { "Routes": { "api": { "Note": "kept" } } } }""");
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        Route(model, "api").Target = "http://10.0.0.3:8080";

        AssertSameJson(
            JsonNode.Parse("""{ "api": { "Note": "kept", "Target": "http://10.0.0.3:8080" } }""")!,
            Written(model.ToChanges(), ManagedSettingKeys.Routes));
    }

    [Fact]
    public void ToChanges_NeverTouchesTheRemotes()
    {
        using var layout = Layout("""{ "DevConduit": { "Remotes": { "uat": { "Scopes": [ "api://custom/.default" ] } } } }""");
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);
        Route(model, "api").Enabled = false;

        var changes = model.ToChanges();

        Assert.Equal([ManagedSettingKeys.Routes], changes.Keys);
        Assert.Equal(["api://custom/.default"], Parse(view.EffectiveWith(changes)).Remotes.Single(remote => remote.Name == "uat").Scopes);
    }

    // ---- Editing an inherited row --------------------------------------------------------

    [Fact]
    public void ToChanges_WritesOnlyTheInheritedRowThatChanged()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        Route(model, "orders").Target = "http://10.50.1.11:8080";

        var changes = model.ToChanges();
        AssertSameJson(
            JsonNode.Parse("""{ "orders": { "Target": "http://10.50.1.11:8080" } }""")!,
            Written(changes, ManagedSettingKeys.Routes));
        Assert.False(model.MatchesInherited(Route(model, "orders")));

        var routing = Parse(view.EffectiveWith(changes));
        Assert.Empty(routing.Problems);
        var orders = routing.Routes.Single(route => route.Name == "orders");
        Assert.Equal("http://10.50.1.11:8080", orders.Target);
        Assert.Equal("prod", orders.Remote);
    }

    [Fact]
    public void ToChanges_LeavesTheUneditedPropertiesOfAnInheritedRowToFollowTheLayersBelow()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        Route(model, "orders").Remote = "uat";
        new SettingsFileWriter(layout.UserPath).Apply(model.ToChanges());

        AssertSameJson(
            JsonNode.Parse("""{ "DevConduit": { "Routes": { "orders": { "Remote": "uat" } } } }""")!,
            new SettingsFileWriter(layout.UserPath).Read());

        // An update replaces the lower file; the user file must not hold the old target.
        layout.AppSettings(BaseSettings.Replace("http://10.50.1.10:8080", "http://10.50.1.12:8080", StringComparison.Ordinal));

        var orders = Parse(layout.View(SettingsTarget.User).Effective).Routes.Single(route => route.Name == "orders");
        Assert.Equal("http://10.50.1.12:8080", orders.Target);
        Assert.Equal("uat", orders.Remote);
    }

    [Fact]
    public void ToChanges_RemovesAnOverriddenPropertySetBackToTheInheritedValue()
    {
        using var layout = Layout("""{ "DevConduit": { "Routes": { "orders": { "Target": "http://10.50.1.11:8080", "Remote": "uat" } } } }""");
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        Route(model, "orders").Target = "http://10.50.1.10:8080";

        var changes = model.ToChanges();
        AssertSameJson(JsonNode.Parse("""{ "orders": { "Remote": "uat" } }""")!, Written(changes, ManagedSettingKeys.Routes));
    }

    [Fact]
    public void ToChanges_SwitchesOffAStringFormInheritedRouteWithoutRestatingItsTarget()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        Route(model, "api").Enabled = false;

        var changes = model.ToChanges();
        AssertSameJson(JsonNode.Parse("""{ "api": { "Enabled": false } }""")!, Written(changes, ManagedSettingKeys.Routes));
        Assert.Equal(["orders"], Parse(view.EffectiveWith(changes)).Routes.Select(route => route.Name));
    }

    [Fact]
    public void ToChanges_ChangingARouteServerToNoneWritesABlankThatSendsItToTheDefault()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        Route(model, "orders").Remote = string.Empty;

        var changes = model.ToChanges();
        AssertSameJson(JsonNode.Parse("""{ "orders": { "Remote": "" } }""")!, Written(changes, ManagedSettingKeys.Routes));

        var routing = Parse(view.EffectiveWith(changes));
        Assert.Null(routing.Routes.Single(route => route.Name == "orders").Remote);
        Assert.Equal("uat", routing.RemoteFor(routing.Routes.Single(route => route.Name == "orders"))?.Name);
    }

    [Fact]
    public void ToChanges_NamingTheDefaultServerOnARouteThatNamesNoneWritesNothing()
    {
        using var layout = Layout();
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        Route(model, "api").Remote = "UAT";

        Assert.True(model.MatchesInherited(Route(model, "api")));
        Assert.Contains(ManagedSettingKeys.Routes, model.ToChanges().Removed);
    }

    // ---- Deleting ------------------------------------------------------------------------

    [Fact]
    public void ToChanges_SwitchesOffADeletedInheritedRoute()
    {
        using var layout = Layout();
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        // The grid turns a delete of an inherited row into clearing its On box.
        Route(model, "orders").Enabled = false;

        var changes = model.ToChanges();
        AssertSameJson(JsonNode.Parse("""{ "orders": { "Enabled": false } }""")!, Written(changes, ManagedSettingKeys.Routes));
        Assert.Equal(["api"], Parse(view.EffectiveWith(changes)).Routes.Select(route => route.Name));
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Validate_ReportsARouteLeftOnASwitchedOffServer()
    {
        using var layout = Layout("""{ "DevConduit": { "Remotes": { "prod": { "Enabled": false } } } }""");
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        Assert.DoesNotContain("prod", model.ServerNames);
        Assert.Contains(model.Validate(), problem => problem.StartsWith("DevConduit:Routes:orders names the remote 'prod'", StringComparison.Ordinal));
    }

    [Fact]
    public void ToChanges_SwitchingAnInheritedRowBackOnDropsItFromTheFile()
    {
        using var layout = Layout("""{ "DevConduit": { "Routes": { "api": { "Enabled": false } } } }""");
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);
        Assert.False(Route(model, "api").Enabled);

        Route(model, "api").Enabled = true;

        var changes = model.ToChanges();
        Assert.Contains(ManagedSettingKeys.Routes, changes.Removed);
        Assert.Equal(["api", "orders"], Parse(view.EffectiveWith(changes)).Routes.Select(route => route.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ToChanges_RemovesADeletedAddedRow()
    {
        using var layout = Layout("""
            { "DevConduit": { "Routes": { "local": "http://localhost:5000", "extra": "http://localhost:5001" } } }
            """);
        var view = layout.View(SettingsTarget.User);
        var model = RoutingEditModel.Load(view);

        model.Routes.Remove(Route(model, "extra"));

        var changes = model.ToChanges();
        Assert.Equal(["local"], Written(changes, ManagedSettingKeys.Routes).Select(pair => pair.Key));
        Assert.DoesNotContain(Parse(view.EffectiveWith(changes)).Routes, route => route.Name == "extra");
    }

    // ---- Adding --------------------------------------------------------------------------

    [Fact]
    public void AddingARoute_RoundTripsThroughTheFileAndTheParser()
    {
        using var layout = Layout();
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        model.Routes.Add(new RouteRow { Name = " records ", Target = " http://10.0.0.9:9000 ", Remote = "prod" });
        Assert.Empty(model.Validate());

        var changes = model.ToChanges();
        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.User));
        new SettingsFileWriter(layout.UserPath).Apply(changes);

        var view = layout.View(SettingsTarget.User);
        var routing = Parse(view.Effective);
        Assert.Empty(routing.Problems);
        var records = routing.Routes.Single(route => route.Name == "records");
        Assert.Equal("http://10.0.0.9:9000", records.Target);
        Assert.Equal("prod", records.Remote);
        Assert.Equal(["api", "orders", "records"], routing.Routes.Select(route => route.Name).Order(StringComparer.Ordinal));

        var reloaded = RoutingEditModel.Load(view);
        Assert.Null(Route(reloaded, "records").Inherited);
        Assert.True(reloaded.MatchesInherited(Route(reloaded, "orders")));
        AssertSameJson(view.Document, ApplyToCopy(view, reloaded.ToChanges()));
    }

    [Fact]
    public void AddingARouteNamingNoServer_LeavesTheServerOut()
    {
        using var layout = Layout();
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));

        model.Routes.Add(new RouteRow { Name = "records", Target = "http://10.0.0.9:9000" });

        AssertSameJson(
            JsonNode.Parse("""{ "records": { "Target": "http://10.0.0.9:9000" } }""")!,
            Written(model.ToChanges(), ManagedSettingKeys.Routes));
    }

    // ---- Allow-lists ---------------------------------------------------------------------

    [Theory]
    [InlineData(SettingsTarget.User)]
    [InlineData(SettingsTarget.Machine)]
    public void ToChanges_TouchesOnlyKeysInTheTargetsAllowList(SettingsTarget target)
    {
        using var layout = Layout();
        var model = RoutingEditModel.Load(layout.View(target));
        Route(model, "api").Target = "http://10.0.0.3:8080";
        Route(model, "orders").Enabled = false;
        model.Routes.Add(new RouteRow { Name = "dev", Target = "http://localhost:5000" });

        var changes = model.ToChanges();

        var allowed = target == SettingsTarget.Machine ? ManagedSettingKeys.Machine : ManagedSettingKeys.User;
        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, allowed));
        Assert.Equal([ManagedSettingKeys.Routes], changes.Keys);
    }

    [Fact]
    public void ToChanges_OfAPartialEntryPassesTheElevatedHelpersCheckAndAppliesToTheMachineFile()
    {
        using var layout = Layout();
        using var temp = new TempDirectory();
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.Machine));
        Route(model, "api").Enabled = false;

        var path = MachineSettingsTransfer.WriteTempFile(model.ToChanges(), temp.Path);
        Assert.True(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var changes, out var error), error);
        new SettingsFileWriter(layout.MachinePath).Apply(changes);

        AssertSameJson(
            JsonNode.Parse("""{ "DevConduit": { "Routes": { "api": { "Enabled": false } } } }""")!,
            new SettingsFileWriter(layout.MachinePath).Read());
        Assert.Equal(["orders"], Parse(layout.View(SettingsTarget.User).Effective).Routes.Select(route => route.Name));
    }

    [Fact]
    public void Load_ForTheMachineFileDoesNotSeeTheUserFile()
    {
        using var layout = Layout("""{ "DevConduit": { "Routes": { "dev": "http://localhost:5000" } } }""");

        var model = RoutingEditModel.Load(layout.View(SettingsTarget.Machine));

        Assert.DoesNotContain(model.Routes, row => row.Name == "dev");
        Assert.Equal(SettingsTarget.Machine, model.Target);
    }

    [Fact]
    public void Validate_ForTheMachineFileIgnoresRemotes()
    {
        using var layout = new SettingsLayout().Machine("""{ "DevConduit": { "Routes": { "api": "http://10.0.0.2:8080" } } }""");

        Assert.Empty(RoutingEditModel.Load(layout.View(SettingsTarget.Machine)).Validate());
        Assert.NotEmpty(RoutingEditModel.Load(layout.View(SettingsTarget.User)).Validate());
    }

    // ---- Row problems --------------------------------------------------------------------

    [Fact]
    public void RowProblems_IsEmptyForTheLoadedRows()
    {
        using var layout = Layout();

        Assert.Empty(RoutingEditModel.Load(layout.View(SettingsTarget.User)).RowProblems());
    }

    [Fact]
    public void RowProblems_ReportsBlankColonAndDuplicateNames()
    {
        using var layout = Layout();
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));
        model.Routes.Add(new RouteRow { Name = " ", Target = "http://x" });
        model.Routes.Add(new RouteRow { Name = "a:b", Target = "http://x" });
        model.Routes.Add(new RouteRow { Name = "API", Target = "http://x" });

        var problems = model.RowProblems();

        Assert.Contains("Every route needs a name.", problems);
        Assert.Contains("The route name 'a:b' contains ':', which configuration reads as a separator.", problems);
        Assert.Contains("There are two routes named 'API'. Names ignore case.", problems);
        Assert.Equal(problems, model.Validate());
    }

    [Fact]
    public void RowProblems_ReportsAnEnabledRowWithoutATargetButNotADisabledOne()
    {
        using var layout = Layout();
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.User));
        model.Routes.Add(new RouteRow { Name = "new" });
        model.Routes.Add(new RouteRow { Name = "off", Enabled = false });

        Assert.Equal(["The route 'new' needs a target, the upstream origin it stands for."], model.RowProblems());
    }
}
