using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The delegated scope a <c>.conduit</c> carries from the server to its clients: optional in the
/// file, a plain scope name when present, and written by the import as the remote's
/// <c>api://{clientId}/{scope}</c> unless it is the default. Pure: nothing is written to disk.
/// </summary>
public sealed class ProxyConfigScopeTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    private readonly X509Certificate2 _server = CertificateFactory.CreateServerCertificate(Host, Now.AddDays(-1));
    private readonly X509Certificate2 _authority = CertificateFactory.CreateClientAuthority(Host, Now.AddDays(-1));
    private readonly X509Certificate2 _client;

    public ProxyConfigScopeTests()
    {
        _client = CertificateFactory.CreateClientCertificate(_authority, CertificateFactory.ClientSubject(Host, Now, TestCertificates.Enrollment), Now.AddDays(-1));
    }

    public void Dispose()
    {
        _server.Dispose();
        _authority.Dispose();
        _client.Dispose();
    }

    private ProxyConfigDocument Document() =>
        new(
            ProxyConfigDocument.CurrentFormat,
            Host,
            "10.0.0.5",
            8091,
            "devconduit",
            Tenant,
            Client,
            new Dictionary<string, string> { ["api"] = "https://api.internal:8443" },
            [Convert.ToBase64String(_server.RawData)],
            Convert.ToBase64String(CertificateFactory.ExportPkcs12(_client)),
            Now);

    private static string Refusal(ProxyConfigDocument document)
    {
        Assert.False(ProxyConfigValidator.TryValidate(document, Now, out var config, out var error));
        Assert.Null(config);
        return error;
    }

    private ValidatedProxyConfig Config() =>
        new(Host, "10.0.0.5", true, 8091, "devconduit", Tenant, Client, [new("api", "https://api.internal")], [_server], [], _server);

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

    // ---- The file and its check -----------------------------------------------------------

    [Fact]
    public void AFileWithoutAScope_IsAcceptedWithNone()
    {
        Assert.True(ProxyConfigValidator.TryValidate(Document(), Now, out var config, out var error), error);

        Assert.Null(config.Scope);
        Assert.DoesNotContain("\"scope\"", Encoding.UTF8.GetString(Document().ToUtf8()), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFileWithoutTheScopeField_StillParses()
    {
        var json = JsonNode.Parse(Document().ToUtf8())!.AsObject();
        json.Remove("scope");

        Assert.True(ProxyConfigValidator.TryValidate(Encoding.UTF8.GetBytes(json.ToJsonString()), Now, out var config, out var error), error);
        Assert.Null(config.Scope);
    }

    [Theory]
    [InlineData("DevProxy.Forward")]
    [InlineData(" DevConduit.Forward ")]
    [InlineData("access_as_user")]
    [InlineData("a-b.c_D9")]
    public void AScopeName_IsCarriedTrimmed(string scope)
    {
        var bytes = (Document() with { Scope = scope }).ToUtf8();

        Assert.Equal(scope, (string?)JsonNode.Parse(bytes)!["scope"]);
        Assert.True(ProxyConfigValidator.TryValidate(bytes, Now, out var config, out var error), error);
        Assert.Equal(scope.Trim(), config.Scope);
    }

    [Fact]
    public void AScopeOf120Characters_IsAccepted() =>
        Assert.True(ProxyConfigValidator.TryValidate(Document() with { Scope = new string('s', 120) }, Now, out _, out var error), error);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api://66666666-7777-8888-9999-000000000000/DevProxy.Forward")]
    [InlineData("Dev Proxy")]
    [InlineData("a/b")]
    [InlineData("scope:read")]
    [InlineData("scope*")]
    [InlineData("scopé")]
    public void AScopeThatIsNotAPlainName_IsRefused(string scope) =>
        Assert.Contains("The scope", Refusal(Document() with { Scope = scope }));

    [Fact]
    public void AScopeOver120Characters_IsRefused() =>
        Assert.Contains("1 to 120", Refusal(Document() with { Scope = new string('s', 121) }));

    // ---- The import ----------------------------------------------------------------------

    private static JsonNode? RemoteScopes(JsonObject settings) =>
        settings["DevConduit"]!["Remotes"]!["devconduit"]!.AsObject()["Scopes"];

    [Fact]
    public void AScopeOtherThanTheDefault_SetsTheRemotesScopes()
    {
        var result = ProxyConfigSettingsMerge.Compute(Empty(), Config() with { Scope = "DevProxy.Forward" }, []);
        var after = Apply(Empty(), result.Changes);

        AssertJson($"""[ "api://{Client}/DevProxy.Forward" ]""", RemoteScopes(after));
        Assert.Contains($"The remote requests the scope api://{Client}/DevProxy.Forward.", result.Notes);
        Assert.Empty(ManagedSettingKeys.Unmanaged(result.Changes, ManagedSettingKeys.Imported));

        // As local mode reads it: the remote requests that scope, not the default.
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(after.ToJsonString())))
            .Build();
        var routing = RoutingConfigurationParser.Parse(configuration.GetSection("DevConduit"), configuration.GetSection("AzureAd"));
        Assert.Equal([$"api://{Client}/DevProxy.Forward"], Assert.Single(routing.Remotes).Scopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("DevConduit.Forward")]
    [InlineData("devconduit.forward")]
    public void NoScopeOrTheDefault_WritesNoScopes(string? scope)
    {
        var result = ProxyConfigSettingsMerge.Compute(Empty(), Config() with { Scope = scope }, []);

        Assert.Null(RemoteScopes(Apply(Empty(), result.Changes)));
        Assert.DoesNotContain(result.Notes, note => note.Contains("scope", StringComparison.Ordinal));
    }

    [Fact]
    public void AReimportWithAnotherScope_ReplacesTheOneAnEarlierImportWrote()
    {
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": { "Remotes": { "devconduit": { "Endpoint": "https://{{Host}}:8091", "Scopes": [ "api://{{Client}}/DevProxy.Forward" ] } } } }
            """);

        var result = ProxyConfigSettingsMerge.Compute(settings, Config() with { Scope = "Other.Forward" }, []);

        AssertJson($"""[ "api://{Client}/Other.Forward" ]""", RemoteScopes(Apply(settings, result.Changes)));
    }

    [Fact]
    public void TheDefaultScope_DropsTheScopesAnEarlierImportWroteSoTheDefaultApplies()
    {
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": { "Remotes": { "devconduit": { "Endpoint": "https://{{Host}}:8091", "Targets": [ "*.corp" ], "Scopes": [ "api://{{Client}}/DevProxy.Forward" ] } } } }
            """);

        var result = ProxyConfigSettingsMerge.Compute(settings, Config() with { Scope = "DevConduit.Forward" }, []);
        var remote = Apply(settings, result.Changes)["DevConduit"]!["Remotes"]!["devconduit"];

        AssertJson($$"""{ "Endpoint": "https://{{Host}}:8091", "Targets": [ "*.corp" ] }""", remote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("DevConduit.Forward")]
    public void ScopesAPersonWrote_StayWhenTheFileNamesNoneOrTheDefault(string? scope)
    {
        var settings = Json($$"""
            { "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
              "DevConduit": { "Remotes": { "devconduit": { "Endpoint": "https://{{Host}}:8091", "Scopes": [ "api://other/one", "api://other/two" ] } } } }
            """);

        var result = ProxyConfigSettingsMerge.Compute(settings, Config() with { Scope = scope }, []);

        AssertJson("""[ "api://other/one", "api://other/two" ]""", RemoteScopes(Apply(settings, result.Changes)));
    }
}
