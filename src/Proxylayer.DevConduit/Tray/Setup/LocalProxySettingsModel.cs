using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>
/// The workstation's local-mode settings as the setup window edits them, for the user
/// settings file. The ports and switches show their merged values. The shared secret is not
/// edited here; a settings file can still set <c>DevConduit:Local:SharedSecret</c>.
/// </summary>
public sealed class LocalProxySettingsModel
{
    private readonly SettingsView _view;
    private readonly LocalModeOptions _inherited;
    private readonly Fields _loaded;

    private LocalProxySettingsModel(SettingsView view)
    {
        _view = view;
        _inherited = Bind(view.Below);
        var effective = Bind(view.Effective);

        Port = effective.Port;
        ForwardProxyEnabled = effective.ForwardProxyEnabled;
        ForwardProxyPort = effective.ForwardProxyPort;
        CaptureEnabled = effective.CaptureEnabled;
        _loaded = Current();
    }

    public int Port { get; set; }

    public bool ForwardProxyEnabled { get; set; }

    public int ForwardProxyPort { get; set; }

    public bool CaptureEnabled { get; set; }

    public static LocalProxySettingsModel Load(SettingsView view) => new(view);

    /// <summary>True when a field differs from what the files gave it when the model was loaded.</summary>
    public bool HasEdits => Current() != _loaded;

    /// <summary>The model read again from <paramref name="view"/>, keeping every field edited since this one was loaded.</summary>
    public LocalProxySettingsModel Reload(SettingsView view)
    {
        var reloaded = Load(view);
        var edited = Current();
        reloaded.Port = Keep(edited.Port, _loaded.Port, reloaded.Port);
        reloaded.ForwardProxyEnabled = Keep(edited.ForwardProxyEnabled, _loaded.ForwardProxyEnabled, reloaded.ForwardProxyEnabled);
        reloaded.ForwardProxyPort = Keep(edited.ForwardProxyPort, _loaded.ForwardProxyPort, reloaded.ForwardProxyPort);
        reloaded.CaptureEnabled = Keep(edited.CaptureEnabled, _loaded.CaptureEnabled, reloaded.CaptureEnabled);
        return reloaded;
    }

    public SettingsChangeSet ToChanges()
    {
        var changes = new SettingsChangeSet();
        SettingValues.Number(changes, ManagedSettingKeys.LocalPort, Port, _inherited.Port);
        SettingValues.Switch(changes, ManagedSettingKeys.LocalForwardProxyEnabled, ForwardProxyEnabled, _inherited.ForwardProxyEnabled);
        SettingValues.Number(changes, ManagedSettingKeys.LocalForwardProxyPort, ForwardProxyPort, _inherited.ForwardProxyPort);
        SettingValues.Switch(changes, ManagedSettingKeys.LocalCaptureEnabled, CaptureEnabled, _inherited.CaptureEnabled);
        return changes;
    }

    /// <summary>The problems local mode itself would refuse to start with.</summary>
    public IReadOnlyList<string> Validate() => Bind(_view.EffectiveWith(ToChanges())).FatalProblems();

    private static LocalModeOptions Bind(IConfiguration configuration) =>
        configuration.GetSection($"{DevConduitOptions.SectionName}:Local").Get<LocalModeOptions>() ?? new LocalModeOptions();

    private Fields Current() => new(Port, ForwardProxyEnabled, ForwardProxyPort, CaptureEnabled);

    /// <summary>The edited value when it was edited, else the value the files give now.</summary>
    private static T Keep<T>(T edited, T loaded, T fresh) =>
        EqualityComparer<T>.Default.Equals(edited, loaded) ? fresh : edited;

    private sealed record Fields(int Port, bool ForwardProxyEnabled, int ForwardProxyPort, bool CaptureEnabled);
}
