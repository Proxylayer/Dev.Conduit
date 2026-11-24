using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>Asks for the address an export tells clients to map the server's host name to.</summary>
[SupportedOSPlatform("windows")]
internal sealed class AddressPrompt : Form
{
    private readonly TextBox _address = new() { Width = 300 };
    private readonly Label _problem = new() { AutoSize = true, ForeColor = Color.DarkRed, Margin = new Padding(3, 0, 3, 6) };

    public AddressPrompt(string hostname, string address)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = "Client address";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _address.Text = address;

        var ok = new Button { Text = "Export", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = $"The IP address clients map {hostname} to. It goes into their hosts file; a name instead must resolve through DNS.",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            Margin = new Padding(3, 3, 3, 8)
        });
        layout.Controls.Add(_address);
        layout.Controls.Add(_problem);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;

        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && ClientConfigExportPlan.AddressProblem(_address.Text) is { } problem)
            {
                _problem.Text = problem;
                _address.Focus();
                e.Cancel = true;
            }
        };
    }

    /// <summary>The address entered, trimmed.</summary>
    public string Address => _address.Text.Trim();
}
