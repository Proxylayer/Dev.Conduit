using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// A "start with Windows" entry: the <c>Dev.Conduit</c> value under
/// <c>Software\Microsoft\Windows\CurrentVersion\Run</c>, holding <c>"&lt;exe&gt;" tray</c>.
/// The tray's own entry is the current user's; the Workstation install writes the same value
/// under HKLM for every user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartWithWindows
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ValueName = "Dev.Conduit";

    private readonly RegistryKey _hive;
    private readonly string _keyPath;

    /// <param name="hive">The hive holding the Run key: the current user's, or the machine's for the installer's entry.</param>
    /// <param name="keyPath">The Run key under the hive.</param>
    /// <param name="executablePath">The exe the entry starts.</param>
    public StartWithWindows(RegistryKey hive, string keyPath, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _hive = hive;
        _keyPath = keyPath;
        Command = $"\"{executablePath}\" tray";
    }

    /// <summary>The entry for the current user and this exe.</summary>
    public static StartWithWindows ForCurrentUser() =>
        new(Registry.CurrentUser, RunKeyPath, ThisExecutable());

    /// <summary>The entry the Workstation install writes for every user. Read only: changing it needs elevation.</summary>
    public static StartWithWindows ForAllUsers() =>
        new(Registry.LocalMachine, RunKeyPath, ThisExecutable());

    private static string ThisExecutable() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Dev.Conduit.exe");

    /// <summary>The command the entry holds when enabled.</summary>
    public string Command { get; }

    /// <summary>True when the entry exists, whichever exe it points at.</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = _hive.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    /// <summary>True when the entry exists and starts some other exe, such as an older copy.</summary>
    public bool PointsElsewhere
    {
        get
        {
            using var key = _hive.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(ValueName) is string { Length: > 0 } value
                && !string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Enable()
    {
        using var key = _hive.CreateSubKey(_keyPath, writable: true);
        key.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = _hive.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
