using System.Runtime.Versioning;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// Saves a change from the setup window. The user settings file is written directly; the
/// machine settings file is written by the elevated helper, from a transfer file this side
/// writes and removes again afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsSaver
{
    private readonly SettingsFiles _files;
    private readonly ServiceAdminClient _admin;

    public SettingsSaver(SettingsFiles files, ServiceAdminClient admin)
    {
        _files = files;
        _admin = admin;
    }

    /// <param name="environmentName">For the machine file only: also set the installed service's environment name.</param>
    public async Task<ServiceAdminOutcome> SaveAsync(SettingsTarget target, SettingsChangeSet changes, string? environmentName = null)
    {
        var allowed = target == SettingsTarget.Machine ? ManagedSettingKeys.Machine : ManagedSettingKeys.User;
        var unmanaged = ManagedSettingKeys.Unmanaged(changes, allowed);
        if (unmanaged.Count > 0)
        {
            return new ServiceAdminOutcome(false, false, $"These settings cannot be saved to the {Describe(target)}: {string.Join(", ", unmanaged)}.");
        }

        if (target == SettingsTarget.User)
        {
            try
            {
                new SettingsFileWriter(_files.UserPath).Apply(changes);
                return new ServiceAdminOutcome(true, false, $"Saved to {_files.UserPath}.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new ServiceAdminOutcome(false, false, exception.Message);
            }
        }

        var transfer = await Task.Run(() => MachineSettingsTransfer.WriteTempFile(changes)).ConfigureAwait(false);
        try
        {
            var outcome = environmentName is null
                ? await _admin.RunAsync(ServiceAdminCommand.SaveAction, transfer).ConfigureAwait(false)
                : await _admin.RunAsync(ServiceAdminCommand.SaveAction, transfer, environmentName).ConfigureAwait(false);

            return outcome.Succeeded ? outcome with { Message = $"Saved to {_files.MachinePath}." } : outcome;
        }
        finally
        {
            MachineSettingsTransfer.Delete(transfer);
        }
    }

    private static string Describe(SettingsTarget target) =>
        target == SettingsTarget.Machine ? "machine settings" : "user settings";
}
