using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// A server seeding its own machine settings from another server's .conduit: the Entra tenant
/// and client ids, the scope and the routes (name to target, no remote), nothing else. Its host name, port,
/// client address, credentials and allow-lists stay as they were, and nothing of the file's
/// certificates, address or remote is written. The machine settings file is a temp file.
/// </summary>
public sealed class ServerSeedSettingsTests : IDisposable
{
    private const string SourceHost = "devconduit.example.internal";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";
    private const string LegacyScope = "DevProxy.Forward";

    private const string ServerSettings = """
        {
          "AzureAd": {
            "TenantId": "contoso.onmicrosoft.com",
            "ClientCredentials": [ { "SourceType": "SignedAssertionFromManagedIdentity", "ManagedIdentityClientId": "22222222-2222-2222-2222-222222222222" } ]
          },
          "DevConduit": {
            "Server": { "Hostname": "devconduit-dev.example.internal", "Port": 9443, "ClientAddress": "10.9.9.9" },
            "AllowedUserOids": [ "aaaaaaaa-0000-0000-0000-000000000001" ],
            "Routes": { "old": "http://old.internal" }
          }
        }
        """;

    private readonly TempDirectory _root = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => _root.Dispose();

    private string MachinePath => Path.Combine(_root.Path, "machine", SettingsFiles.FileName);

    /// <summary>A real export from another server, checked as the seed checks it.</summary>
    private ValidatedProxyConfig Exported(string extension = ProxyConfigDocument.FileExtension, string? scope = LegacyScope)
    {
        var authority = new ServerCertificateAuthority(ServerCertificateStore.ForDataRoot(Path.Combine(_root.Path, "source")), _clock, NullLogger.Instance);
        var path = Path.Combine(_root.Path, "devconduit" + ProxyConfigDocument.FileExtension);
        new ProxyConfigExporter(authority, _clock).Export(
            new ProxyConfigExportRequest(SourceHost, "10.0.0.5", 8091, Tenant, Client, [new("api", "https://api.internal:8443"), new("web", "https://web.internal")], scope),
            path);

        var copy = Path.ChangeExtension(path, extension);
        if (copy != path)
        {
            File.Copy(path, copy);
        }

        Assert.True(ProxyConfigValidator.TryValidate(ProxyConfigTransfer.ReadLimited(copy), _clock.GetUtcNow(), out var config, out var error), error);
        return config;
    }

    private JsonObject Machine() => SettingsJson.Parse(File.ReadAllText(MachinePath));

    private static string? At(JsonObject document, string path)
    {
        JsonNode? node = document;
        foreach (var segment in path.Split(':'))
        {
            node = node is JsonObject parent ? parent[segment] : null;
        }

        return node is JsonValue value ? value.ToString() : node?.ToJsonString();
    }

    [Fact]
    public void Changes_TouchOnlyTheSeedKeys()
    {
        var changes = ServerSeedSettings.From(Exported()).Changes;

        Assert.Equal(ManagedSettingKeys.Seed.Order(StringComparer.Ordinal), changes.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.Seed));
        Assert.DoesNotContain(changes.Keys, key => key.StartsWith("DevConduit:Server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Changes_WriteTheRoutesAsNameAndTargetWithNoRemote()
    {
        var routes = Assert.IsType<JsonObject>(ServerSeedSettings.From(Exported()).Changes.Values[ManagedSettingKeys.Routes]);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{ "api": "https://api.internal:8443", "web": "https://web.internal" }"""),
            routes));
    }

    [Fact]
    public void Changes_RemoveTheRoutesWhenTheFileHasNone()
    {
        var seed = new ServerSeedSettings(Tenant, Client, []);

        Assert.Contains(ManagedSettingKeys.Routes, seed.Changes.Removed);
    }

    [Fact]
    public void Describe_SaysWhatChanges()
    {
        var lines = ServerSeedSettings.From(Exported()).Describe(SettingsJson.Parse(ServerSettings));

        Assert.Equal(
            [
                $"Tenant id: {Tenant} (now contoso.onmicrosoft.com)",
                $"Client id: {Client} (not set now)",
                $"Scope: {LegacyScope} (now DevConduit.Forward)",
                "Routes: 2 (api, web), replacing the 1 route set now"
            ],
            lines);
    }

    [Fact]
    public void Changes_SetTheScopeTheFileNames()
    {
        var scopes = Assert.IsType<JsonArray>(ServerSeedSettings.From(Exported()).Changes.Values[ManagedSettingKeys.Scopes]);

        Assert.Equal([LegacyScope], scopes.Select(scope => (string?)scope));
    }

    [Fact]
    public void Changes_RemoveTheScopeWhenTheFileNamesTheDefault()
    {
        var changes = ServerSeedSettings.From(Exported(scope: "DevConduit.Forward")).Changes;

        Assert.Contains(ManagedSettingKeys.Scopes, changes.Removed);
        Assert.False(changes.Values.ContainsKey(ManagedSettingKeys.Scopes));
    }

    [Fact]
    public void Changes_LeaveTheScopeAloneWhenTheFileHasNone()
    {
        var changes = ServerSeedSettings.From(Exported(scope: null)).Changes;

        Assert.DoesNotContain(ManagedSettingKeys.Scopes, changes.Keys);
        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.Seed));
    }

    [Fact]
    public void Describe_SaysTheScopeStaysWhenTheFileHasNone()
    {
        var lines = ServerSeedSettings.From(Exported(scope: null)).Describe(SettingsJson.Parse(ServerSettings));

        Assert.Contains("Scope: not in the file, left as it is", lines);
    }

    [Theory]
    [InlineData(ProxyConfigDocument.FileExtension)]
    [InlineData(ProxyConfigDocument.LegacyFileExtension)]
    public void Seed_WritesTheEntraIdsAndRoutesAndKeepsEverythingElse(string extension)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MachinePath)!);
        File.WriteAllText(MachinePath, ServerSettings);
        using var output = new StringWriter();

        var exitCode = ImportCommand.Seed(Exported(extension), MachinePath, protect: false, output);

        Assert.Equal(ServiceAdminExitCode.Success, exitCode);
        var machine = Machine();
        Assert.Equal(Tenant, At(machine, "AzureAd:TenantId"));
        Assert.Equal(Client, At(machine, "AzureAd:ClientId"));
        Assert.Equal(LegacyScope, (string?)machine["AzureAd"]!["Scopes"]![0]);
        Assert.Equal("https://api.internal:8443", At(machine, "DevConduit:Routes:api"));
        Assert.Equal("https://web.internal", At(machine, "DevConduit:Routes:web"));
        Assert.Null(At(machine, "DevConduit:Routes:old"));

        // This server's own settings stay; nothing of the file's server, address or remote comes in.
        Assert.Equal("devconduit-dev.example.internal", At(machine, "DevConduit:Server:Hostname"));
        Assert.Equal("9443", At(machine, "DevConduit:Server:Port"));
        Assert.Equal("10.9.9.9", At(machine, "DevConduit:Server:ClientAddress"));
        Assert.NotNull(At(machine, "AzureAd:ClientCredentials"));
        Assert.NotNull(At(machine, "DevConduit:AllowedUserOids"));
        Assert.Null(At(machine, "DevConduit:Remotes"));
        Assert.DoesNotContain(SourceHost, File.ReadAllText(MachinePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.0.0.5", File.ReadAllText(MachinePath), StringComparison.Ordinal);
        Assert.Contains(ServerSeedSettings.ManagedIdentityNote, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Seed_CreatesTheMachineSettingsFileWhenThereIsNone()
    {
        using var output = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.Success, ImportCommand.Seed(Exported(), MachinePath, protect: false, output));

        Assert.Equal(Tenant, At(Machine(), "AzureAd:TenantId"));
        Assert.Null(At(Machine(), "DevConduit:Server"));
    }

    [Fact]
    public void Seed_RefusesAMachineSettingsFileThatIsNotJson()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MachinePath)!);
        File.WriteAllText(MachinePath, "{ not json");
        using var output = new StringWriter();

        Assert.Equal(ServiceAdminExitCode.SettingsWriteFailed, ImportCommand.Seed(Exported(), MachinePath, protect: false, output));
        Assert.Equal("{ not json", File.ReadAllText(MachinePath));
    }
}
