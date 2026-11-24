namespace Proxylayer.DevConduit.Tray;

/// <summary>The status colours the tray icon shows.</summary>
public enum TrayStatusKind
{
    /// <summary>Grey.</summary>
    Stopped,

    /// <summary>Amber: starting, stopping or signing in.</summary>
    Busy,

    /// <summary>Green.</summary>
    Running,

    /// <summary>Amber: running, but something needs looking at.</summary>
    Attention,

    /// <summary>Red.</summary>
    Failed
}

/// <summary>What the tray shows: the icon colour and the status line.</summary>
public sealed record TrayStatus(TrayStatusKind Kind, string Text);
