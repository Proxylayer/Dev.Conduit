using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>Menu items and shell actions the two roles build their menus from.</summary>
[SupportedOSPlatform("windows")]
internal static class TrayMenu
{
    public static ToolStripMenuItem Item(string text, Action action, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>An item whose action is asynchronous; a failure is logged and shown rather than lost.</summary>
    public static ToolStripMenuItem Item(string text, Func<Task> action, TrayServices services, bool enabled = true) =>
        Item(text, () => Run(action, text, services), enabled);

    /// <summary>A disabled item in bold, for the menu's header and status lines.</summary>
    public static ToolStripMenuItem Label(string text, bool bold = false)
    {
        var item = new ToolStripMenuItem(text) { Enabled = false };
        if (bold)
        {
            item.Font = new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
        }

        return item;
    }

    public static async void Run(Func<Task> action, string what, TrayServices services)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            services.Logger.LogError(exception, "{Action} failed.", what);
            services.Notify("Dev.Conduit", $"{what} failed: {exception.Message}", ToolTipIcon.Error, services.OpenLog);
        }
    }

    /// <summary>Opens an address in the default browser.</summary>
    public static void OpenInBrowser(Uri address, TrayServices services)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception exception)
        {
            services.Logger.LogWarning("The browser could not be opened at {Address}: {Message}", address, exception.Message);
        }
    }
}
