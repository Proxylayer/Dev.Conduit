using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tray;

/// <summary>Whether the tray starts the local proxy on its own, and why not when it does not.</summary>
public sealed record LocalProxyAutoStartDecision(bool Start, string Reason);

/// <summary>
/// Decides whether a workstation's tray starts the local proxy without a click: when it starts,
/// and after a save or an import leaves the proxy stopped. It does only when
/// <c>DevConduit:Tray:AutoStart</c> is on (the default), at least one remote is configured,
/// local mode has no fatal configuration problem, and at least one remote has a usable Entra
/// tenant and client id to sign in with. Pure: reads only the configuration passed in, the
/// files a start would read.
/// </summary>
public static class LocalProxyAutoStart
{
    public const string Key = ManagedSettingKeys.TrayAutoStart;

    /// <summary>The <c>DevConduit:Tray:AutoStart</c> switch; anything but <c>false</c> leaves it on.</summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !bool.TryParse(configuration[Key]?.Trim(), out var enabled) || enabled;
    }

    public static LocalProxyAutoStartDecision Decide(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsEnabled(configuration))
        {
            return new LocalProxyAutoStartDecision(false, "Start proxy automatically is off.");
        }

        var devConduitSection = configuration.GetSection(DevConduitOptions.SectionName);
        var routing = RoutingConfigurationParser.Parse(devConduitSection, configuration.GetSection("AzureAd"));
        if (routing.Remotes.Count == 0)
        {
            return new LocalProxyAutoStartDecision(false, "No server is imported yet.");
        }

        var devConduit = devConduitSection.Get<DevConduitOptions>() ?? new DevConduitOptions();
        if (LocalModeStartup.Validate(devConduit, routing) is { Count: > 0 } problems)
        {
            return new LocalProxyAutoStartDecision(false, $"The configuration has a problem: {problems[0]}");
        }

        if (!routing.Remotes.Any(HasUsableIdentity))
        {
            return new LocalProxyAutoStartDecision(false, "No server has an Entra tenant and client id to sign in with.");
        }

        return new LocalProxyAutoStartDecision(true, $"{routing.Remotes.Count} server{(routing.Remotes.Count == 1 ? string.Empty : "s")} configured.");
    }

    private static bool HasUsableIdentity(RemoteDefinition remote) =>
        remote.Identity.IsComplete
        && !StartupDiagnostics.IsPlaceholder(remote.Identity.TenantId)
        && !StartupDiagnostics.IsPlaceholder(remote.Identity.ClientId);
}
