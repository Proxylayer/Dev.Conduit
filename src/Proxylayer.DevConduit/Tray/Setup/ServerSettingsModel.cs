using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// The server's settings as the setup window edits them, for the machine settings file: the
/// host name, port and client address of the app-managed HTTPS endpoint, the Entra ids,
/// managed identity and delegated scope, who may sign in, and which hosts it may call. Text and
/// list fields show only what the file holds, blank meaning the layers below decide, and expose
/// the inherited value for display; the port
/// shows its merged value. A blank host name, with none below, means the computer's name
/// (<see cref="ServerHostnameDefault"/>), which is shown but never written unless typed in.
/// Nothing about certificates is a setting: the server manages its own.
/// </summary>
public sealed class ServerSettingsModel
{
    private readonly SettingsView _view;
    private readonly int _inheritedPort;
    private readonly Fields _loaded;
    private readonly string? _computerName;
    private readonly string? _defaultHostname;

    private ServerSettingsModel(SettingsView view, string? computerName)
    {
        _view = view;
        _inheritedPort = PortOf(view.Below);
        _computerName = computerName;
        _defaultHostname = ServerHostnameDefault.For(view.Below, computerName is not null, () => computerName);

        Hostname = view.Override[ManagedSettingKeys.ServerHostname] ?? string.Empty;
        Port = PortOf(view.Effective);
        ClientAddress = view.Override[ManagedSettingKeys.ServerClientAddress] ?? string.Empty;
        TenantId = view.Override[ManagedSettingKeys.TenantId] ?? string.Empty;
        ClientId = view.Override[ManagedSettingKeys.ClientId] ?? string.Empty;
        ManagedIdentityClientId = ManagedIdentityOf(view.Override) ?? string.Empty;
        Scope = string.Join(", ", SettingValues.ReadList(view.Override.GetSection(ManagedSettingKeys.Scopes)));
        AllowedTargets = string.Join(Environment.NewLine, SettingValues.ReadList(view.Override.GetSection(ManagedSettingKeys.AllowedTargets)));
        AllowedUserOids = string.Join(Environment.NewLine, SettingValues.ReadList(view.Override.GetSection(ManagedSettingKeys.AllowedUserOids)));
        AllowedGroupIds = string.Join(Environment.NewLine, SettingValues.ReadList(view.Override.GetSection(ManagedSettingKeys.AllowedGroupIds)));
        _loaded = Current();
    }

    /// <summary>The name clients reach the server by; it turns on HTTPS with the app-managed certificate.</summary>
    public string Hostname { get; set; }

    public int Port { get; set; }

    /// <summary>The IP address (or name) clients map the host name to, written into exports.</summary>
    public string ClientAddress { get; set; }

    public string TenantId { get; set; }

    public string ClientId { get; set; }

    /// <summary>The user-assigned managed identity's client id, written as the first <c>AzureAd:ClientCredentials</c> entry.</summary>
    public string ManagedIdentityClientId { get; set; }

    /// <summary>
    /// The delegated scope bearer callers must carry, its plain name; blank means
    /// <see cref="ApiScopeOptions.DefaultScope"/>. Exports hand it to clients.
    /// </summary>
    public string Scope { get; set; }

    /// <summary>One <c>DevConduit:Route:AllowedTargets</c> entry per line: a host, a <c>*.</c> wildcard, an address or a network.</summary>
    public string AllowedTargets { get; set; }

    /// <summary>One object id per line.</summary>
    public string AllowedUserOids { get; set; }

    /// <summary>One group id per line.</summary>
    public string AllowedGroupIds { get; set; }

    /// <param name="computerName">
    /// This computer's name, the host name a server uses when no file names one; null where
    /// that default does not apply.
    /// </param>
    public static ServerSettingsModel Load(SettingsView view, string? computerName = null) => new(view, computerName);

    /// <summary>
    /// The host name the server uses while <see cref="Hostname"/> is blank and no layer below
    /// names one: this computer's name. Null when a host name is set or no default applies.
    /// </summary>
    public string? DefaultHostname => Hostname.Trim().Length == 0 ? _defaultHostname : null;

    /// <summary>True when a field differs from what the files gave it when the model was loaded.</summary>
    public bool HasEdits => Current() != _loaded;

    /// <summary>
    /// The model read again from <paramref name="view"/>, the files as they are now, keeping
    /// every field edited since this one was loaded. Another page's save then changes only
    /// what it saved, and this page's own save merges into the file as it is.
    /// </summary>
    public ServerSettingsModel Reload(SettingsView view)
    {
        var reloaded = Load(view, _computerName);
        var edited = Current();
        reloaded.Hostname = Keep(edited.Hostname, _loaded.Hostname, reloaded.Hostname);
        reloaded.Port = Keep(edited.Port, _loaded.Port, reloaded.Port);
        reloaded.ClientAddress = Keep(edited.ClientAddress, _loaded.ClientAddress, reloaded.ClientAddress);
        reloaded.TenantId = Keep(edited.TenantId, _loaded.TenantId, reloaded.TenantId);
        reloaded.ClientId = Keep(edited.ClientId, _loaded.ClientId, reloaded.ClientId);
        reloaded.ManagedIdentityClientId = Keep(edited.ManagedIdentityClientId, _loaded.ManagedIdentityClientId, reloaded.ManagedIdentityClientId);
        reloaded.Scope = Keep(edited.Scope, _loaded.Scope, reloaded.Scope);
        reloaded.AllowedTargets = Keep(edited.AllowedTargets, _loaded.AllowedTargets, reloaded.AllowedTargets);
        reloaded.AllowedUserOids = Keep(edited.AllowedUserOids, _loaded.AllowedUserOids, reloaded.AllowedUserOids);
        reloaded.AllowedGroupIds = Keep(edited.AllowedGroupIds, _loaded.AllowedGroupIds, reloaded.AllowedGroupIds);
        return reloaded;
    }

    /// <summary>The value the layers below give a text field, for display beside a blank one.</summary>
    public string? Inherited(string key) => key switch
    {
        ManagedSettingKeys.ClientCredentials => ManagedIdentityOf(_view.Below),
        ManagedSettingKeys.AllowedUserOids or ManagedSettingKeys.AllowedGroupIds or ManagedSettingKeys.AllowedTargets or ManagedSettingKeys.Scopes =>
            string.Join(", ", SettingValues.ReadList(_view.Below.GetSection(key))) is { Length: > 0 } list ? list : null,
        _ => _view.Below[key]
    };

    /// <summary>Where <paramref name="key"/>'s inherited value comes from, or null when nothing below sets it.</summary>
    public string? InheritedFrom(string key) => _view.SourceOf(key);

    /// <summary>
    /// The settings the server would run with once saved: the host name, port and client address
    /// merged, the host name defaulting to <see cref="DefaultHostname"/>.
    /// </summary>
    public ServerHostOptions EffectiveServer()
    {
        var server = _view.EffectiveWith(ToChanges()).GetSection(ServerHostOptions.SectionName).Get<ServerHostOptions>() ?? new ServerHostOptions();
        if (!server.IsManaged && DefaultHostname is { } hostname)
        {
            server.Hostname = hostname;
        }

        return server;
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Hostname.Trim() is { Length: > 0 } hostname && !DnsHostname.IsValid(DnsHostname.Normalize(hostname)))
        {
            problems.Add($"The host name '{hostname}' is not a DNS host name, for example devconduit.example.internal.");
        }

        if (Port is < 1 or > 65535)
        {
            problems.Add($"The port {Port} is not a TCP port.");
        }

        if (ClientAddress.Trim() is { Length: > 0 } address && !DnsHostname.IsAddressOrHostname(address))
        {
            problems.Add($"The client address '{address}' is not an IP address or host name.");
        }

        if (TenantId.Trim() is { Length: > 0 } tenant && !Guid.TryParse(tenant, out _) && Uri.CheckHostName(tenant) != UriHostNameType.Dns)
        {
            problems.Add($"The tenant id '{tenant}' is neither a GUID nor a domain name.");
        }

        RequireGuid("client id", ClientId, problems);
        RequireGuid("managed identity client id", ManagedIdentityClientId, problems);

        foreach (var oid in SettingValues.Split(AllowedUserOids))
        {
            RequireGuid("allowed user object id", oid, problems);
        }

        foreach (var group in SettingValues.Split(AllowedGroupIds))
        {
            RequireGuid("allowed group id", group, problems);
        }

        foreach (var scope in SettingValues.Split(Scope).Where(scope => !ProxyConfigValidator.IsScopeName(scope)))
        {
            problems.Add($"The scope '{scope}' is not a scope name: 1 to {ProxyConfigValidator.MaxScopeLength} letters, digits, dots, dashes or underscores, such as {ApiScopeOptions.DefaultScope}, not its api:// URI.");
        }

        problems.AddRange(new TargetAllowList(SettingValues.Split(AllowedTargets), []).Problems.Select(problem => $"Allowed targets: {problem}"));

        return problems;
    }

    /// <summary>
    /// What the server would leave open once saved: every tenant user when no users or groups
    /// are allowed, and every reachable host when no targets are (<see cref="ServerExposureWarnings"/>).
    /// </summary>
    public IReadOnlyList<string> Warnings()
    {
        var effective = _view.EffectiveWith(ToChanges());
        return ServerExposureWarnings.For(
            SettingValues.ReadList(effective.GetSection(ManagedSettingKeys.AllowedUserOids)),
            SettingValues.ReadList(effective.GetSection(ManagedSettingKeys.AllowedGroupIds)),
            SettingValues.ReadList(effective.GetSection(ManagedSettingKeys.AllowedTargets)));
    }

    public SettingsChangeSet ToChanges()
    {
        var changes = new SettingsChangeSet();

        var hostname = Hostname.Trim().Length == 0 ? string.Empty : DnsHostname.Normalize(Hostname);
        SettingValues.Text(changes, ManagedSettingKeys.ServerHostname, hostname, _view.Below[ManagedSettingKeys.ServerHostname]);
        SettingValues.Number(changes, ManagedSettingKeys.ServerPort, Port, _inheritedPort);
        SettingValues.Text(changes, ManagedSettingKeys.ServerClientAddress, ClientAddress, _view.Below[ManagedSettingKeys.ServerClientAddress]);
        SettingValues.Text(changes, ManagedSettingKeys.TenantId, TenantId, _view.Below[ManagedSettingKeys.TenantId]);
        SettingValues.Text(changes, ManagedSettingKeys.ClientId, ClientId, _view.Below[ManagedSettingKeys.ClientId]);

        var identity = ManagedIdentityClientId.Trim();
        if (identity.Length == 0 || string.Equals(identity, ManagedIdentityOf(_view.Below), StringComparison.OrdinalIgnoreCase))
        {
            changes.Remove(ManagedSettingKeys.ClientCredentials);
        }
        else
        {
            changes.Set(ManagedSettingKeys.ClientCredentials, new JsonArray(new JsonObject
            {
                ["SourceType"] = EntraCredential.ManagedIdentitySourceType,
                ["ManagedIdentityClientId"] = identity
            }));
        }

        // The default scope needs no entry, unless a lower file names another.
        var scopes = SettingValues.Split(Scope);
        if (scopes.Count == 1
            && ApiScopeOptions.IsDefault(scopes[0])
            && SettingValues.ReadList(_view.Below.GetSection(ManagedSettingKeys.Scopes)).Count == 0)
        {
            changes.Remove(ManagedSettingKeys.Scopes);
        }
        else
        {
            SettingValues.List(changes, ManagedSettingKeys.Scopes, scopes, _view.Below);
        }

        SettingValues.List(changes, ManagedSettingKeys.AllowedTargets, SettingValues.Split(AllowedTargets), _view.Below);
        SettingValues.List(changes, ManagedSettingKeys.AllowedUserOids, SettingValues.Split(AllowedUserOids), _view.Below);
        SettingValues.List(changes, ManagedSettingKeys.AllowedGroupIds, SettingValues.Split(AllowedGroupIds), _view.Below);

        return changes;
    }

    private Fields Current() =>
        new(Hostname, Port, ClientAddress, TenantId, ClientId, ManagedIdentityClientId, Scope, AllowedTargets, AllowedUserOids, AllowedGroupIds);

    /// <summary>The edited value when it was edited, else the value the files give now.</summary>
    private static T Keep<T>(T edited, T loaded, T fresh) =>
        EqualityComparer<T>.Default.Equals(edited, loaded) ? fresh : edited;

    /// <summary>The user-assigned identity's client id in a configuration's <c>AzureAd</c>, or null.
    private static string? ManagedIdentityOf(IConfiguration configuration)
    {
        var credential = EntraCredential.Describe(configuration.GetSection("AzureAd"));
        return credential.Kind == EntraCredentialKind.ManagedIdentity ? credential.ManagedIdentityClientId : null;
    }

    private static int PortOf(IConfiguration configuration) =>
        (configuration.GetSection(ServerHostOptions.SectionName).Get<ServerHostOptions>() ?? new ServerHostOptions()).Port;

    private static void RequireGuid(string label, string value, List<string> problems)
    {
        if (value.Trim() is { Length: > 0 } trimmed && !Guid.TryParse(trimmed, out _))
        {
            problems.Add($"The {label} '{trimmed}' is not a GUID.");
        }
    }

    /// <summary>The fields as they stand, to tell an edited one from one the files gave.</summary>
    private sealed record Fields(
        string Hostname,
        int Port,
        string ClientAddress,
        string TenantId,
        string ClientId,
        string ManagedIdentityClientId,
        string Scope,
        string AllowedTargets,
        string AllowedUserOids,
        string AllowedGroupIds);
}
