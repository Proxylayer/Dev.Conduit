using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// "Export client configuration", from the Server page and the server tray menu alike: where to
/// save <c>&lt;hostname&gt;.conduit</c> (Documents by default), the address clients map the
/// host name to, then the elevated helper's <c>export</c>, which issues a client certificate.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ClientConfigExportFlow
{
    private const string Caption = "Export client configuration";

    /// <summary>The file written, or null when nothing was.</summary>
    public static async Task<string?> RunAsync(IWin32Window? owner, ServiceAdminClient admin, IConfiguration machineConfiguration)
    {
        var plan = ClientConfigExportPlan.From(machineConfiguration);
        if (plan.Problem is { } problem)
        {
            MessageBox.Show(owner, problem, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        string path;
        using (var dialog = new SaveFileDialog
        {
            Title = Caption,
            Filter = $"Dev.Conduit client configuration (*{ProxyConfigDocument.FileExtension})|*{ProxyConfigDocument.FileExtension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = plan.FileName,
            DefaultExt = ProxyConfigDocument.FileExtension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = true
        })
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK)
            {
                return null;
            }

            path = dialog.FileName;
        }

        if (ClientConfigExportPlan.PathProblem(path) is { } pathProblem)
        {
            MessageBox.Show(owner, pathProblem, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        using var prompt = new AddressPrompt(plan.Hostname, plan.ClientAddress);
        if (prompt.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        var outcome = await admin.RunAsync(ServiceAdminCommand.ExportAction, path, prompt.Address);
        if (outcome.Cancelled)
        {
            return null;
        }

        if (!outcome.Succeeded)
        {
            MessageBox.Show(owner, outcome.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        MessageBox.Show(
            owner,
            $"Wrote {path}.{Environment.NewLine}{Environment.NewLine}{ClientConfigExportPlan.SecretNote}",
            Caption,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return path;
    }
}
