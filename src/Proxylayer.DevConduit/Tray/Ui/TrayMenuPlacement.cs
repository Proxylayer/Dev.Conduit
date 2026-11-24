using System.Drawing;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// Where the tray menu goes, in physical pixels: above and to the left of the anchor (the tray
/// icon's bottom-right corner), so the menu's bottom-right corner sits on the anchor and the
/// cursor lands inside the menu. It flips below or to the right when the monitor has no room,
/// as it does for a taskbar at the top or left, and is kept within the monitor's bounds. The
/// bounds, not the work area, so a menu opening above the icon may extend down over the taskbar.
/// </summary>
internal static class TrayMenuPlacement
{
    public static Point Locate(Point anchor, Size menu, Rectangle monitor)
    {
        var x = anchor.X - menu.Width;
        if (x < monitor.Left)
        {
            x = anchor.X;
        }

        var y = anchor.Y - menu.Height;
        if (y < monitor.Top)
        {
            y = anchor.Y;
        }

        return new Point(Clamp(x, monitor.Left, monitor.Right - menu.Width), Clamp(y, monitor.Top, monitor.Bottom - menu.Height));
    }

    /// <summary>Keeps <paramref name="value"/> within the range, preferring the low end when the menu is larger than the monitor.</summary>
    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));
}
