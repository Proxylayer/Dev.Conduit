using System.ComponentModel;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// The routes as the setup window edits them, for one settings file. <c>DevConduit:Routes</c> is
/// written to the file whole, and each entry in it holds only the properties that differ from
/// the layers below, beside whatever the file already held that the window does not edit, so
/// everything else still follows those layers. A route the layers below define cannot be
/// deleted from a file above them, so it is switched off instead, with <c>"Enabled": false</c>,
/// which the routing parser honours. The servers a route can name are the remotes the layers
/// give, which imports write; the window never edits them. With one server, a route naming none
/// goes to it; with several, each route must name one.
/// </summary>
public sealed class RoutingEditModel
{
    private const string TargetProperty = "Target";
    private const string RemoteProperty = "Remote";

    /// <summary>What a workstation with no imported server is told: local mode cannot start without one.</summary>
    public const string NoServerProblem = "No server is imported yet, so the local proxy cannot start. Import a .conduit on the Servers page.";

    private readonly SettingsView _view;
    private readonly IReadOnlyList<RouteRow> _loaded;

    /// <param name="loaded">The rows as the files gave them, to tell edits apart; copied.</param>
    private RoutingEditModel(
        SettingsView view,
        IReadOnlyList<RouteRow> loaded,
        IEnumerable<RouteRow> routes,
        IReadOnlyList<string> serverNames,
        string defaultServer)
    {
        _view = view;
        _loaded = loaded.Select(Copy).ToList();
        Routes = new BindingList<RouteRow>(routes.ToList());
        ServerNames = serverNames;
        DefaultServer = defaultServer;
    }

    public BindingList<RouteRow> Routes { get; }

    /// <summary>The servers (remotes) a route can name, switched on in the merged configuration.</summary>
    public IReadOnlyList<string> ServerNames { get; }

    /// <summary>The server a route naming none goes to: the only one, or blank when there are several or none.</summary>
    public string DefaultServer { get; }

    public SettingsTarget Target => _view.Target;

    public static RoutingEditModel Load(SettingsView view)
    {
        var routes = view.Effective.GetSection(ManagedSettingKeys.Routes).GetChildren()
            .Select(section =>
            {
                var below = view.Below.GetSection(section.Path);
                return below.Exists()
                    ? ReadRoute(section, ReadRoute(below, null, null), view.SourceOf(section.Path))
                    : ReadRoute(section, null, null);
            })
            .ToList();

        var routing = Parse(view.Effective);
        return new RoutingEditModel(
            view,
            routes,
            routes,
            routing.Remotes.Select(remote => remote.Name).ToList(),
            routing.DefaultRemote?.Name ?? string.Empty);
    }

    /// <summary>True when a row was added, removed or changed since the model was loaded.</summary>
    public bool HasEdits
    {
        get
        {
            var rows = Routes.Where(route => !IsBlank(route)).ToList();
            return rows.Count != _loaded.Count || rows.Zip(_loaded).Any(pair => !SameValues(pair.First, pair.Second));
        }
    }

    /// <summary>
    /// The model read again from <paramref name="view"/>, the files as they are now, keeping the
    /// rows edited since this one was loaded: a changed row keeps its values over the file's,
    /// an added row stays, and a deleted row stays deleted unless a lower file defines it.
    /// Rows nobody edited follow the files, including ones another save added or removed.
    /// </summary>
    public RoutingEditModel Reload(SettingsView view)
    {
        var reloaded = Load(view);
        if (!HasEdits)
        {
            return reloaded;
        }

        var loaded = _loaded.ToDictionary(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var pending = Routes.Where(route => !IsBlank(route)).ToList();
        var rows = new List<RouteRow>();

        foreach (var fresh in reloaded.Routes)
        {
            var edited = pending.FirstOrDefault(row => string.Equals(row.Name.Trim(), fresh.Name, StringComparison.OrdinalIgnoreCase));
            if (edited is not null)
            {
                pending.Remove(edited);
                rows.Add(Unchanged(edited, loaded) ? fresh : WithValues(fresh, edited));
            }
            else if (!loaded.ContainsKey(fresh.Name) || fresh.Inherited is not null)
            {
                // New in the files, or deleted here but defined below, where deleting means switching off.
                rows.Add(fresh);
            }
        }

        // Rows the files no longer hold: kept when added or changed here, dropped when untouched.
        rows.AddRange(pending.Where(row => !Unchanged(row, loaded)).Select(row => new RouteRow
        {
            Name = row.Name,
            Target = row.Target,
            Remote = row.Remote,
            Enabled = row.Enabled
        }));

        return new RoutingEditModel(view, reloaded._loaded, rows, reloaded.ServerNames, reloaded.DefaultServer);
    }

    /// <summary>The server a row goes to: its own, else <see cref="DefaultServer"/>.</summary>
    public string ServerOf(RouteRow row) => row.Remote.Trim() is { Length: > 0 } remote ? remote : DefaultServer;

    /// <summary>True when an inherited row still goes where the layers below send it, its other values aside.</summary>
    public bool MatchesInherited(RouteRow row) =>
        row.Inherited is { } inherited
        && string.Equals(row.Target.Trim(), inherited.Target.Trim(), StringComparison.Ordinal)
        && string.Equals(ServerOf(row), ServerOf(inherited), StringComparison.OrdinalIgnoreCase)
        && row.Enabled == inherited.Enabled;

    /// <summary>
    /// True for a row with no name and no target that no lower file defines: the grid's row for a
    /// new route, not yet typed into. It is neither checked nor saved.
    /// </summary>
    public static bool IsBlank(RouteRow row) =>
        row.Inherited is null && row.Name.Trim().Length == 0 && row.Target.Trim().Length == 0;

    /// <summary>Problems with the rows themselves, which stop the configuration being built at all.</summary>
    public IReadOnlyList<string> RowProblems()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Routes.Where(route => !IsBlank(route)).Select(route => route.Name.Trim()))
        {
            if (name.Length == 0)
            {
                problems.Add("Every route needs a name.");
            }
            else if (name.Contains(':'))
            {
                problems.Add($"The route name '{name}' contains ':', which configuration reads as a separator.");
            }
            else if (!seen.Add(name))
            {
                problems.Add($"There are two routes named '{name}'. Names ignore case.");
            }
        }

        problems.AddRange(Routes
            .Where(route => route.Enabled && route.Name.Trim().Length > 0 && route.Target.Trim().Length == 0)
            .Select(route => $"The route '{route.Name.Trim()}' needs a target, the upstream origin it stands for."));

        return problems;
    }

    /// <summary>
    /// Every problem a launch would report about the routes: the rows' own, then the route
    /// table's, and for a workstation the routing parser's, run against the configuration the
    /// file would give once saved. A server ignores remotes, so its problems with them are not shown.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = RowProblems();
        if (problems.Count > 0)
        {
            return problems;
        }

        var routing = Parse(_view.EffectiveWith(ToChanges()));
        var aliases = new RouteAliasTable(routing).Problems;
        if (Target == SettingsTarget.Machine)
        {
            return [.. aliases];
        }

        // The parser words a missing remote for whoever edits the file; here an import adds one.
        return routing.Remotes.Count == 0
            ? [NoServerProblem, .. aliases]
            : [.. routing.Problems, .. aliases];
    }

    /// <summary>
    /// The change to the settings file: the routes section whole. Each row starts from the file's
    /// current entry and holds only what differs from the layers below.
    /// </summary>
    public SettingsChangeSet ToChanges()
    {
        var changes = new SettingsChangeSet();
        var existingRoutes = SettingValues.Find(_view.Document, ManagedSettingKeys.Routes);
        var routes = new JsonObject();

        foreach (var row in Routes.Where(route => !IsBlank(route)))
        {
            var name = row.Name.Trim();
            var below = _view.Below.GetSection($"{ManagedSettingKeys.Routes}:{name}");
            var inherited = row.Inherited ?? new RouteRow();
            var (key, existing) = ExistingEntry(existingRoutes, name);
            var entry = StartEntry(existing);

            var target = row.Target.Trim();
            SetUnlessInherited(entry, TargetProperty, target, string.Equals(target, inherited.Target.Trim(), StringComparison.Ordinal));

            // A blank server goes to the default one, so it matches an inherited route naming that server, and the reverse.
            var remote = row.Remote.Trim();
            var sameServer = row.Inherited is null
                ? remote.Length == 0
                : string.Equals(ServerOf(row), ServerOf(inherited), StringComparison.OrdinalIgnoreCase);
            SetUnlessInherited(entry, RemoteProperty, remote, sameServer);
            SetEnabled(entry, row.Enabled, inherited.Enabled);

            if (Finish(entry, existing, below) is { } node)
            {
                routes[key ?? name] = node;
            }
        }

        if (routes.Count == 0)
        {
            changes.Remove(ManagedSettingKeys.Routes);
        }
        else
        {
            changes.Set(ManagedSettingKeys.Routes, routes);
        }

        return changes;
    }

    private static bool Unchanged(RouteRow row, IReadOnlyDictionary<string, RouteRow> loaded) =>
        loaded.TryGetValue(row.Name.Trim(), out var original) && SameValues(row, original);

    private static bool SameValues(RouteRow left, RouteRow right) =>
        string.Equals(left.Name.Trim(), right.Name.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Target.Trim(), right.Target.Trim(), StringComparison.Ordinal)
        && string.Equals(left.Remote.Trim(), right.Remote.Trim(), StringComparison.OrdinalIgnoreCase)
        && left.Enabled == right.Enabled;

    private static RouteRow Copy(RouteRow row) => WithValues(row, row);

    /// <summary>A row with <paramref name="values"/>' target, server and switch, and <paramref name="source"/>'s name and inheritance.</summary>
    private static RouteRow WithValues(RouteRow source, RouteRow values) =>
        new()
        {
            Name = source.Name,
            Target = values.Target,
            Remote = values.Remote,
            Enabled = values.Enabled,
            Inherited = source.Inherited,
            InheritedFrom = source.InheritedFrom
        };

    private static RoutingConfiguration Parse(IConfiguration configuration) =>
        RoutingConfigurationParser.Parse(configuration.GetSection(DevConduitOptions.SectionName), configuration.GetSection("AzureAd"));

    private static RouteRow ReadRoute(IConfigurationSection section, RouteRow? inherited, string? inheritedFrom) =>
        new()
        {
            Name = section.Key,
            Target = FirstNonBlank(section[TargetProperty], section.Value),
            Remote = section[RemoteProperty]?.Trim() ?? string.Empty,
            Enabled = RoutingConfigurationParser.IsEnabled(section),
            Inherited = inherited,
            InheritedFrom = inherited is null ? null : inheritedFrom ?? "below"
        };

    /// <summary>The file's current entry for <paramref name="name"/>, matched ignoring case, and the name it has there.</summary>
    private static (string? Key, JsonNode? Node) ExistingEntry(JsonObject? section, string name)
    {
        var key = section?.Select(property => property.Key)
            .FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        return key is null ? (null, null) : (key, section![key]);
    }

    /// <summary>
    /// A copy of the file's entry to change, so properties the window does not edit are kept.
    /// An entry in string form is its target alone.
    /// </summary>
    private static JsonObject StartEntry(JsonNode? existing) => existing switch
    {
        JsonObject entry => (JsonObject)entry.DeepClone(),
        JsonValue value when value.TryGetValue<string>(out var text) => new JsonObject { [TargetProperty] = text },
        _ => new JsonObject()
    };

    /// <summary>
    /// The entry to write, or null when nothing is left in it. An entry the file held in string
    /// form keeps that form while its target is all it holds and the layers below do not give a
    /// <c>Target</c> property, which would otherwise win over the string.
    /// </summary>
    private static JsonNode? Finish(JsonObject entry, JsonNode? existing, IConfigurationSection below)
    {
        if (entry.Count == 0)
        {
            return null;
        }

        if (existing is JsonValue
            && entry.Count == 1
            && string.Equals(entry.Single().Key, TargetProperty, StringComparison.OrdinalIgnoreCase)
            && entry.Single().Value is JsonValue value
            && value.TryGetValue<string>(out var text)
            && string.IsNullOrWhiteSpace(below[TargetProperty]))
        {
            return JsonValue.Create(text);
        }

        return entry;
    }

    private static void SetUnlessInherited(JsonObject entry, string property, string value, bool matchesInherited)
    {
        if (matchesInherited)
        {
            SettingValues.RemoveProperty(entry, property);
        }
        else
        {
            SettingValues.SetProperty(entry, property, value);
        }
    }

    private static void SetEnabled(JsonObject entry, bool enabled, bool inherited)
    {
        if (enabled == inherited)
        {
            SettingValues.RemoveProperty(entry, RoutingConfigurationParser.EnabledKey);
        }
        else
        {
            SettingValues.SetProperty(entry, RoutingConfigurationParser.EnabledKey, enabled);
        }
    }

    private static string FirstNonBlank(string? preferred, string? fallback) =>
        preferred?.Trim() is { Length: > 0 } value ? value : fallback?.Trim() ?? string.Empty;
}
