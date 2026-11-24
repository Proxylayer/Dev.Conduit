using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// How an import changes the machine settings file, and how removing the server reverses it:
/// the remote, the AzureAd ids only where blank, routes replaced per server, and nothing
/// outside the import allow-list. Pure: every document is in memory.
/// </summary>
public sealed class ProxyConfigSettingsMergeTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";

    private readonly X509Certificate2 _certificate = CertificateFactory.CreateServerCertificate(Host, TestCertificates.Now);

    public void Dispose() => _certificate.Dispose();

    private ValidatedProxyConfig Config(
        string remote = "devconduit",
        string hostname = Host,
        int port = 8091,
        string tenant = Tenant,
        string client = Client,
        params (string Name, string Target)[] routes) =>
        new(
            hostname,
            "10.0.0.5",
            true,
            port,
            remote,
            tenant,
            client,
            routes.Length == 0
                ? [new("api", "https://api.internal"), new("web", "https://web.internal")]
                : [.. routes.Select(route => new KeyValuePair<string, string>(route.Name, route.Target))],
            [_certificate],
            [],
            _certificate);

    // Parsed as the settings writer reads the file: property names match ignoring case.
    private static JsonObject Json(string text) => SettingsJson.Parse(text);

    private static JsonObject Empty() => new(SettingsJson.NodeOptions);

    private static JsonObject Apply(JsonObject settings, SettingsChangeSet changes)
    {
        var copy = (JsonObject)settings.DeepClone();
        changes.ApplyTo(copy);
        return copy;
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"Expected {expected}{Environment.NewLine}Actual   {actual?.ToJsonString()}");

    // ---- Remote --------------------------------------------------------------------------

    [Fact]
    public void IntoAnEmptyFile_AddsTheRemoteRoutesAndAzureAd()
    {
        var result = ProxyConfigSettingsMerge.Compute(Empty(), Config(), []);
        var after = Apply(Empty(), result.Changes);

        AssertJson(
            $$"""
            {
              "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": {
                "Remotes": { "devconduit": { "Endpoint": "https://{{Host}}:8091" } },
                "Routes": {
                  "api": { "Target": "https://api.internal", "Remote": "devconduit" },
                  "web": { "Target": "https://web.internal", "Remote": "devconduit" }
                }
              }
            }
            """,
            after);
        Assert.Equal(["api", "web"], result.ImportedRoutes);
        Assert.Contains(result.Notes, note => note.StartsWith("AzureAd was blank", StringComparison.Ordinal));
    }

    [Fact]
    public void Changes_StayWithinTheImportAllowList()
    {
        var settings = Json("""{ "AzureAd": { "TenantId": "", "ClientId": "" }, "DevConduit": { "Mode": "local", "Local": { "Port": 1 } } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        Assert.Empty(ManagedSettingKeys.Unmanaged(result.Changes, ManagedSettingKeys.Imported));
        Assert.Equal(
            [ManagedSettingKeys.ClientId, ManagedSettingKeys.TenantId, ManagedSettingKeys.Remotes, ManagedSettingKeys.Routes],
            result.Changes.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnUpdate_ReplacesTheRemotesEndpointAndKeepsItsHostRulesAndScopes()
    {
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": { "Remotes": {
                "DEVCONDUIT": { "Endpoint": "https://{{Host}}:9000", "Targets": [ "*.corp" ], "Scopes": [ "x" ] },
                "other": "https://other.example:8091" } } }
            """);

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(port: 8443), []);
        var after = Apply(settings, result.Changes);

        AssertJson(
            $$"""
            { "devconduit": { "Endpoint": "https://{{Host}}:8443", "Targets": [ "*.corp" ], "Scopes": [ "x" ] },
              "other": "https://other.example:8091" }
            """,
            after["DevConduit"]!["Remotes"]);
    }

    [Fact]
    public void AnUpdate_KeepsEveryKeyItDoesNotManageAndUpdatesOnlyWhatItDoes()
    {
        // The remote names its own tenant, which the import's tenant (the AzureAd one) makes redundant.
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": { "Remotes": {
                "devconduit": { "endpoint": "https://{{Host}}:9000", "TenantId": "99999999-9999-9999-9999-999999999999",
                                "Scopes": [ "api://x/.default" ], "Timeout": 30, "Custom": { "a": 1 } } } } }
            """);

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);
        var after = Apply(settings, result.Changes);

        AssertJson(
            $$"""
            { "devconduit": { "endpoint": "https://{{Host}}:8091", "Scopes": [ "api://x/.default" ], "Timeout": 30, "Custom": { "a": 1 } } }
            """,
            after["DevConduit"]!["Remotes"]);
    }

    [Fact]
    public void ARemoteNameTakenByAnotherServer_IsRefused()
    {
        var settings = Json("""{ "DevConduit": { "Remotes": { "Devconduit": { "Endpoint": "https://someone-else.example" } } } }""");

        var refusal = Assert.Throws<InvalidOperationException>(() => ProxyConfigSettingsMerge.Compute(settings, Config(), []));

        Assert.Contains("'Devconduit'", refusal.Message);
    }

    [Fact]
    public void ARemoteNameTakenByAStringEndpointForThisServer_IsUpdated()
    {
        var settings = Json($$"""{ "DevConduit": { "Remotes": { "devconduit": "https://{{Host}}:8091" } } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        AssertJson($$"""{ "devconduit": { "Endpoint": "https://{{Host}}:8091" } }""", result.Changes.Values[ManagedSettingKeys.Remotes]);
    }

    // ---- AzureAd -------------------------------------------------------------------------

    [Fact]
    public void AzureAdAlreadySet_IsLeftAloneAndTheRemoteCarriesItsOwnIds()
    {
        var settings = Json("""{ "AzureAd": { "TenantId": "aaaaaaaa-0000-0000-0000-000000000000", "ClientId": "bbbbbbbb-0000-0000-0000-000000000000" } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        Assert.DoesNotContain(ManagedSettingKeys.TenantId, result.Changes.Keys);
        Assert.DoesNotContain(ManagedSettingKeys.ClientId, result.Changes.Keys);
        AssertJson(
            $$"""{ "devconduit": { "Endpoint": "https://{{Host}}:8091", "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" } }""",
            result.Changes.Values[ManagedSettingKeys.Remotes]);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void AzureAdMatchingTheFile_IsLeftAloneAndTheRemoteInheritsIt()
    {
        var settings = Json($$"""{ "azuread": { "tenantid": "{{Tenant.ToUpperInvariant()}}", "clientid": "{{Client}}" } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        Assert.DoesNotContain(ManagedSettingKeys.TenantId, result.Changes.Keys);
        AssertJson($$"""{ "devconduit": { "Endpoint": "https://{{Host}}:8091" } }""", result.Changes.Values[ManagedSettingKeys.Remotes]);
    }

    [Theory]
    [InlineData("", "bbbbbbbb-0000-0000-0000-000000000000")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000000", "  ")]
    [InlineData("<your-tenant-id>", "bbbbbbbb-0000-0000-0000-000000000000")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000000", "YOUR-CLIENT-ID")]
    public void AzureAdHalfBlankOrAPlaceholder_IsFilledFromTheFileWhole(string tenant, string client)
    {
        var settings = Json($$"""{ "AzureAd": { "TenantId": "{{tenant}}", "ClientId": "{{client}}" } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        AssertJson($"\"{Tenant}\"", result.Changes.Values[ManagedSettingKeys.TenantId]);
        AssertJson($"\"{Client}\"", result.Changes.Values[ManagedSettingKeys.ClientId]);
    }

    // ---- Routes --------------------------------------------------------------------------

    [Fact]
    public void RoutesFromTheSameRemote_AreReplacedWhole()
    {
        var settings = Json("""
            { "DevConduit": { "Routes": {
                "api": { "Target": "https://old-api.internal", "Remote": "DEVCONDUIT" },
                "gone": { "Target": "https://gone.internal", "Remote": "devconduit" },
                "mine": "https://mine.internal" } } }
            """);

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        AssertJson(
            """
            { "mine": "https://mine.internal",
              "api": { "Target": "https://api.internal", "Remote": "devconduit" },
              "web": { "Target": "https://web.internal", "Remote": "devconduit" } }
            """,
            result.Changes.Values[ManagedSettingKeys.Routes]);
    }

    [Fact]
    public void ARouteNameAnotherRemoteUses_IsKeptAndReported()
    {
        var settings = Json("""{ "DevConduit": { "Routes": { "API": { "Target": "https://theirs.internal", "Remote": "other" } } } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        AssertJson(
            """
            { "API": { "Target": "https://theirs.internal", "Remote": "other" },
              "web": { "Target": "https://web.internal", "Remote": "devconduit" } }
            """,
            result.Changes.Values[ManagedSettingKeys.Routes]);
        Assert.Equal(["web"], result.ImportedRoutes);
        Assert.Contains("Route 'api' was not imported: the name is already used by a route for the remote 'other'.", result.Notes);
    }

    [Fact]
    public void ARouteNameTheDefaultRemoteUses_IsKeptAndReported()
    {
        var settings = Json("""{ "DevConduit": { "Routes": { "api": "https://mine.internal" } } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        AssertJson("\"https://mine.internal\"", result.Changes.Values[ManagedSettingKeys.Routes]["api"]);
        Assert.Contains("Route 'api' was not imported: the name is already used by a route for the default remote.", result.Notes);
    }

    [Fact]
    public void ARouteThisServersEarlierImportAddedWithoutARemote_IsReplaced()
    {
        // Records written by an earlier version may name routes that carry no Remote.
        var settings = Json("""{ "DevConduit": { "Routes": { "api": "https://old.internal", "kept": "https://kept.internal" } } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), ["API", "kept-not-listed"]);

        Assert.Equal(["api", "web"], result.ImportedRoutes);
        AssertJson(
            """
            { "kept": "https://kept.internal",
              "api": { "Target": "https://api.internal", "Remote": "devconduit" },
              "web": { "Target": "https://web.internal", "Remote": "devconduit" } }
            """,
            result.Changes.Values[ManagedSettingKeys.Routes]);
    }

    [Fact]
    public void APreviouslyImportedNameNowOwnedByAnotherRemote_IsKept()
    {
        var settings = Json("""{ "DevConduit": { "Routes": { "api": { "Target": "https://theirs.internal", "Remote": "other" } } } }""");

        var result = ProxyConfigSettingsMerge.Compute(settings, Config(), ["api"]);

        Assert.Equal(["web"], result.ImportedRoutes);
    }

    [Fact]
    public void ImportingTheSameFileTwice_GivesTheSameDocument()
    {
        var settings = Json("""{ "DevConduit": { "Remotes": { "other": "https://other.example" }, "Routes": { "x": { "Target": "https://x.internal", "Remote": "other" } } } }""");

        var first = ProxyConfigSettingsMerge.Compute(settings, Config(), []);
        var once = Apply(settings, first.Changes);
        var second = ProxyConfigSettingsMerge.Compute(once, Config(), first.ImportedRoutes);
        var twice = Apply(once, second.Changes);

        Assert.True(JsonNode.DeepEquals(once, twice), twice.ToJsonString());
        Assert.Equal(first.ImportedRoutes, second.ImportedRoutes);
    }

    [Fact]
    public void Compute_DoesNotChangeTheDocumentItIsGiven()
    {
        var settings = Json("""{ "DevConduit": { "Routes": { "api": { "Target": "https://old.internal", "Remote": "devconduit" } } } }""");
        var before = settings.ToJsonString();

        ProxyConfigSettingsMerge.Compute(settings, Config(), []);

        Assert.Equal(before, settings.ToJsonString());
    }

    // ---- Removal -------------------------------------------------------------------------

    private static ProxyConfigImportRecord Record(string remote = "devconduit", params string[] routes) =>
        new(Host, remote, routes, TestCertificates.Now, "10.0.0.5");

    [Fact]
    public void Removal_TakesOutTheRemoteAndEveryRouteNamingIt()
    {
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": {
                "Remotes": { "devconduit": { "Endpoint": "https://{{Host}}:8091" }, "other": "https://other.example" },
                "Routes": {
                  "api": { "Target": "https://api.internal", "Remote": "DEVCONDUIT" },
                  "x": { "Target": "https://x.internal", "Remote": "other" },
                  "mine": "https://mine.internal" } } }
            """);

        var removal = ImportedServerSettingsRemoval.Compute(settings, Host.ToUpperInvariant(), Record(routes: "api"));
        var after = Apply(settings, removal.Changes);

        Assert.Equal("devconduit", removal.Remote);
        Assert.Equal(["api"], removal.RemovedRoutes);
        AssertJson(
            $$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": {
                "Remotes": { "other": "https://other.example" },
                "Routes": { "x": { "Target": "https://x.internal", "Remote": "other" }, "mine": "https://mine.internal" } } }
            """,
            after);
        Assert.Empty(ManagedSettingKeys.Unmanaged(removal.Changes, ManagedSettingKeys.Imported));
    }

    [Fact]
    public void Removal_ReversesAnImportIntoAFileThatHadOtherServers()
    {
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": {
                "Remotes": { "other": "https://other.example" },
                "Routes": { "x": { "Target": "https://x.internal", "Remote": "other" }, "api": { "Target": "https://theirs.internal", "Remote": "other" } } } }
            """);
        var merge = ProxyConfigSettingsMerge.Compute(settings, Config(), []);
        var imported = Apply(settings, merge.Changes);

        var removal = ImportedServerSettingsRemoval.Compute(imported, Host, Record(routes: [.. merge.ImportedRoutes]));
        var restored = Apply(imported, removal.Changes);

        Assert.True(JsonNode.DeepEquals(settings, restored), restored.ToJsonString());
        Assert.Equal(["web"], removal.RemovedRoutes);
    }

    [Fact]
    public void Removal_OfTheOnlyServer_RemovesTheEmptySectionsButKeepsAzureAd()
    {
        var merge = ProxyConfigSettingsMerge.Compute(Empty(), Config(), []);
        var imported = Apply(Empty(), merge.Changes);

        var removal = ImportedServerSettingsRemoval.Compute(imported, Host, Record(routes: [.. merge.ImportedRoutes]));
        var after = Apply(imported, removal.Changes);

        Assert.Equal([ManagedSettingKeys.Remotes, ManagedSettingKeys.Routes], removal.Changes.Removed.Order(StringComparer.Ordinal));
        AssertJson($$"""{ "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" } }""", after);
    }

    [Fact]
    public void Removal_FindsARenamedRemoteByItsEndpoint()
    {
        var settings = Json($$"""
            { "DevConduit": {
                "Remotes": { "renamed": { "Endpoint": "https://{{Host}}:8091" }, "devconduit": "https://elsewhere.example" },
                "Routes": { "a": { "Target": "https://a.internal", "Remote": "renamed" }, "b": { "Target": "https://b.internal", "Remote": "devconduit" } } } }
            """);

        var removal = ImportedServerSettingsRemoval.Compute(settings, Host, Record());
        var after = Apply(settings, removal.Changes);

        Assert.Equal("renamed", removal.Remote);
        AssertJson(
            """
            { "Remotes": { "devconduit": "https://elsewhere.example" },
              "Routes": { "b": { "Target": "https://b.internal", "Remote": "devconduit" } } }
            """,
            after["DevConduit"]);
    }

    [Fact]
    public void Removal_WithoutARecord_StillRemovesTheRemotePointingAtTheServer()
    {
        var settings = Json($$"""{ "DevConduit": { "Remotes": { "x": "https://{{Host}}" }, "Routes": { "r": { "Target": "https://r.internal", "Remote": "x" } } } }""");

        var removal = ImportedServerSettingsRemoval.Compute(settings, Host, record: null);

        Assert.Equal("x", removal.Remote);
        Assert.Equal(["r"], removal.RemovedRoutes);
    }

    [Fact]
    public void Removal_WhenNothingNamesTheServer_ChangesNothing()
    {
        var settings = Json("""{ "DevConduit": { "Remotes": { "other": "https://other.example" }, "Routes": { "api": "https://api.internal" } } }""");

        var removal = ImportedServerSettingsRemoval.Compute(settings, Host, Record(routes: "unrelated"));

        Assert.True(removal.Changes.IsEmpty);
        Assert.Null(removal.Remote);
        Assert.Empty(removal.RemovedRoutes);
    }

    [Fact]
    public void Removal_RemovesRecordedRoutesWithoutAnOwnerEvenWithoutARemote()
    {
        var settings = Json("""{ "DevConduit": { "Routes": { "api": "https://api.internal", "keep": "https://keep.internal" } } }""");

        var removal = ImportedServerSettingsRemoval.Compute(settings, Host, Record(routes: "API"));

        Assert.Null(removal.Remote);
        Assert.Equal(["api"], removal.RemovedRoutes);
        AssertJson("""{ "keep": "https://keep.internal" }""", removal.Changes.Values[ManagedSettingKeys.Routes]);
        Assert.DoesNotContain(ManagedSettingKeys.Remotes, removal.Changes.Keys);
    }

    [Fact]
    public void Removal_DoesNotTouchARemotePointingAtAHostThatMerelyStartsTheSame()
    {
        var settings = Json($$"""{ "DevConduit": { "Remotes": { "near": "https://{{Host}}.evil.example" } } }""");

        var removal = ImportedServerSettingsRemoval.Compute(settings, Host, record: null);

        Assert.True(removal.Changes.IsEmpty);
    }
}
