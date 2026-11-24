using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>
/// One page of the setup window. Each reloads from the settings files whenever any page saves:
/// the page that saved takes the files as they now are, and every other page keeps what was
/// typed into it and not yet saved, on top of the files as they now are.
/// </summary>
[SupportedOSPlatform("windows")]
internal abstract class SetupTab : UserControl
{
    protected SetupTab()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
    }

    /// <summary>Raised after the page saved a settings file.</summary>
    public event EventHandler<SettingsTarget>? Saved;

    /// <summary>The title on the page's tab.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Reads the page's values from the settings files. With <paramref name="keepEdits"/>, values
    /// changed on the page since it last loaded are kept over what the files now hold.
    /// </summary>
    public abstract void LoadFrom(SettingsLayers layers, bool keepEdits);

    protected void OnSaved(SettingsTarget target) => Saved?.Invoke(this, target);

    /// <summary>Asks whether to save despite problems. True to go ahead.</summary>
    protected bool ConfirmDespite(IReadOnlyList<string> problems, string what) =>
        problems.Count == 0
        || MessageBox.Show(
            this,
            $"{what} has problems:{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, problems.Select(problem => "- " + problem))}"
            + $"{Environment.NewLine}{Environment.NewLine}Save anyway?",
            "Dev.Conduit setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;

    /// <summary>Runs a button's asynchronous work; a failure is shown rather than lost.</summary>
    protected async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowFailure(exception.Message);
        }
    }

    protected void ShowFailure(string message) =>
        MessageBox.Show(this, message, "Dev.Conduit setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
