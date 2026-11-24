namespace Proxylayer.DevConduit.Tray.Updates;

public enum TrayUpdateAction
{
    /// <summary>Nothing to do: no package is ready, or this tray does not install updates.</summary>
    None,

    /// <summary>Offer the update: a menu item and one balloon, installed when the user clicks.</summary>
    Offer,

    /// <summary>Start the install now, which shows the UAC prompt once.</summary>
    Install
}

/// <summary>
/// What the tray does with a verified package. Only a workstation's tray installs updates; on a
/// server the service does. With Install updates automatically on, the install starts by itself,
/// unless the user declined its UAC prompt during this tray's run, or it failed less than a day
/// ago; then, as with the switch off, the update is offered and waits for a click. Pure.
/// </summary>
public static class TrayUpdateDecision
{
    public static TrayUpdateAction Decide(
        bool installsUpdates,
        bool packageReady,
        bool autoInstall,
        bool declinedThisRun,
        DateTimeOffset? retryNotBefore,
        DateTimeOffset now)
    {
        if (!installsUpdates || !packageReady)
        {
            return TrayUpdateAction.None;
        }

        var mayRetry = retryNotBefore is not { } notBefore || now >= notBefore;
        return autoInstall && !declinedThisRun && mayRetry ? TrayUpdateAction.Install : TrayUpdateAction.Offer;
    }
}
