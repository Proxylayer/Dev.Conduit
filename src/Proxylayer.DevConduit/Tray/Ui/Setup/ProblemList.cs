using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>The problems a check found, one per line, or a line saying there are none.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ProblemList : TextBox
{
    public ProblemList()
    {
        Multiline = true;
        ReadOnly = true;
        ScrollBars = ScrollBars.Vertical;
        BackColor = SystemColors.Window;
        Dock = DockStyle.Fill;
    }

    public void ShowProblems(IReadOnlyList<string> problems, string none = "No problems found.")
    {
        ForeColor = problems.Count == 0 ? Color.DarkGreen : Color.DarkRed;
        Text = problems.Count == 0 ? none : string.Join(Environment.NewLine, problems.Select(problem => "- " + problem));
    }

    public void ShowError(string message)
    {
        ForeColor = Color.DarkRed;
        Text = message;
    }
}
