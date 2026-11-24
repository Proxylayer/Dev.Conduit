namespace Proxylayer.DevConduit.Tray.Setup;

/// <summary>The settings file the setup window writes to.</summary>
public enum SettingsTarget
{
    /// <summary><c>%ProgramData%\Proxylayer\Dev.Conduit\settings.json</c>, written elevated; read by every launch.</summary>
    Machine,

    /// <summary><c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit\settings.json</c>; read by console and tray launches.</summary>
    User
}
