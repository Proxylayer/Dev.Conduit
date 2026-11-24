using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>
/// The four configuration files a launch reads, laid out under a throwaway directory: a content
/// root holding appsettings.json and appsettings.Uat.json, and machine and user settings files
/// in folders of their own. Never the real ProgramData or LocalAppData files.
/// </summary>
internal sealed class SettingsLayout : IDisposable
{
    public const string EnvironmentName = "Uat";

    private readonly TempDirectory _root = new();

    public SettingsLayout()
    {
        ContentRoot = Path.Combine(_root.Path, "app");
        Directory.CreateDirectory(ContentRoot);

        MachinePath = Path.Combine(_root.Path, "machine", SettingsFiles.FileName);
        UserPath = Path.Combine(_root.Path, "user", SettingsFiles.FileName);
        Files = new SettingsFiles(MachinePath, UserPath);
    }

    public string Root => _root.Path;

    public string ContentRoot { get; }

    public string AppSettingsPath => Path.Combine(ContentRoot, "appsettings.json");

    public string EnvironmentSettingsPath => Path.Combine(ContentRoot, $"appsettings.{EnvironmentName}.json");

    public string MachinePath { get; }

    public string UserPath { get; }

    public SettingsFiles Files { get; }

    public SettingsLayout AppSettings(string json) => Write(AppSettingsPath, json);

    public SettingsLayout EnvironmentSettings(string json) => Write(EnvironmentSettingsPath, json);

    public SettingsLayout Machine(string json) => Write(MachinePath, json);

    public SettingsLayout User(string json) => Write(UserPath, json);

    public SettingsLayers Layers() => new(ContentRoot, EnvironmentName, Files);

    public SettingsView View(SettingsTarget target) => Layers().View(target);

    public void Dispose() => _root.Dispose();

    private SettingsLayout Write(string path, string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return this;
    }
}
