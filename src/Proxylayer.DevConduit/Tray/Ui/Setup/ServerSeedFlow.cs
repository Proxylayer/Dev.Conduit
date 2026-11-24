using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// "Copy settings from .conduit", on the Server page: picks another server's <c>.conduit</c>,
/// checks it here, unelevated, as strictly as an import, shows what it would change in the
/// machine settings file (the tenant and client ids and the routes) and asks to go ahead, then
/// hands a copy to the elevated helper's <c>seed</c>. The host name, port and client address
/// stay this server's own, and the file's certificates are not used.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServerSeedFlow
{
    private const string Caption = "Copy settings from .conduit";

    /// <summary>True when the machine settings file was changed.</summary>
    /// <param name="machineSettings">The machine settings file as it is now, to say what changes.</param>
    public static async Task<bool> RunAsync(IWin32Window owner, ServiceAdminClient admin, JsonObject machineSettings)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Copy settings from a Dev.Conduit server's client configuration",
            Filter = $"Dev.Conduit client configuration (*{ProxyConfigDocument.FileExtension}, *{ProxyConfigDocument.LegacyFileExtension})"
                + $"|*{ProxyConfigDocument.FileExtension};*{ProxyConfigDocument.LegacyFileExtension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return false;
        }

        var name = Path.GetFileName(dialog.FileName);
        ValidatedProxyConfig config;
        try
        {
            if (!ProxyConfigValidator.TryValidate(ProxyConfigTransfer.ReadLimited(dialog.FileName), DateTimeOffset.UtcNow, out var validated, out var error))
            {
                Show(owner, $"Nothing was copied from {name}:{Environment.NewLine}{Environment.NewLine}{error}", MessageBoxIcon.Error);
                return false;
            }

            config = validated;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Show(owner, $"The file could not be read: {exception.Message}", MessageBoxIcon.Error);
            return false;
        }

        var seed = ServerSeedSettings.From(config);
        var lines = string.Join(Environment.NewLine, seed.Describe(machineSettings).Select(line => "- " + line));
        var confirm = MessageBox.Show(
            owner,
            $"Copy these settings from {name} (server {config.Hostname}) into this server's machine settings?{Environment.NewLine}{Environment.NewLine}"
            + lines + Environment.NewLine + Environment.NewLine
            + "The host name, port and client address stay this server's own, and the file's certificates are not used. "
            + ServerSeedSettings.ManagedIdentityNote,
            Caption,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return false;
        }

        var transfer = await Task.Run(() => ProxyConfigTransfer.WriteTempFile(dialog.FileName));
        ServiceAdminOutcome outcome;
        try
        {
            outcome = await admin.RunAsync(ServiceAdminCommand.SeedAction, transfer);
        }
        finally
        {
            ProxyConfigTransfer.Delete(transfer);
        }

        if (outcome.Cancelled)
        {
            return false;
        }

        if (!outcome.Succeeded)
        {
            Show(owner, outcome.Message, MessageBoxIcon.Error);
            return false;
        }

        return true;
    }

    private static void Show(IWin32Window owner, string message, MessageBoxIcon icon) =>
        MessageBox.Show(owner, message, Caption, MessageBoxButtons.OK, icon);
}
