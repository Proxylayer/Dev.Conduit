using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// Opens the tray menu at the notification-area icon. NotifyIcon's own display puts the menu at
/// the cursor, sized for whichever monitor the menu last sat on; with monitors at different
/// scales the menu then lands in the wrong place. This anchors it to the icon's rectangle and,
/// once the menu has taken on the scale of the icon's monitor, places it again with its new size
/// until the scale stops changing.
/// </summary>
/// <remarks>
/// Each opening gets a new menu window. A menu window takes its scale from the monitor it is
/// created on, and once created it does not reliably follow a move to a monitor of lower scale.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TrayMenuPresenter : IDisposable
{
    /// <summary>A move can change the menu's scale, and with it its size; two passes settle it in practice.</summary>
    private const int MaxPlacementPasses = 3;

    private readonly NotifyIcon _icon;
    private readonly Action<ToolStripItemCollection> _fill;
    private ContextMenuStrip? _menu;
    private Point _anchor;
    private Rectangle _monitor;

    /// <param name="icon">The icon the menu opens at.</param>
    /// <param name="fill">Adds the menu's items; called on each opening and on <see cref="Refresh"/>.</param>
    public TrayMenuPresenter(NotifyIcon icon, Action<ToolStripItemCollection> fill)
    {
        _icon = icon;
        _fill = fill;
    }

    public void Show()
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip();
        _fill(_menu.Items);

        var iconWindow = IconWindow();
        _anchor = iconWindow is { } window && TryGetIconRect(window.Handle, window.Id, out var rect)
            ? new Point(rect.Right, rect.Bottom)
            : Cursor.Position;
        _monitor = Screen.FromPoint(_anchor).Bounds;

        // The menu closes on a click elsewhere only when this app is in the foreground.
        if (iconWindow is { } owner)
        {
            SetForegroundWindow(owner.Handle);
        }

        _menu.Show(TrayMenuPlacement.Locate(_anchor, _menu.Size, _monitor));
        if (iconWindow is null)
        {
            SetForegroundWindow(_menu.Handle);
        }

        Settle(_menu);
    }

    /// <summary>Rebuilds an open menu's items, which can change its size, and places it again at its anchor.</summary>
    public void Refresh()
    {
        if (_menu is not { Visible: true } menu)
        {
            return;
        }

        foreach (var item in menu.Items.Cast<ToolStripItem>().ToList())
        {
            item.Dispose();
        }

        menu.Items.Clear();
        _fill(menu.Items);
        Settle(menu);
    }

    public void Dispose()
    {
        _menu?.Dispose();
        _menu = null;
    }

    /// <summary>
    /// Moves the menu so its current size sits at the anchor. Moving it can carry it onto a monitor
    /// with another scale, which resizes it, so it is placed again until its DPI holds still.
    /// The move goes through SetWindowPos because the menu's own bounds keep it off the taskbar,
    /// and it raises the menu to the top of the topmost windows so the taskbar does not cover it.
    /// </summary>
    private void Settle(ContextMenuStrip menu)
    {
        var handle = menu.Handle;
        var dpi = GetDpiForWindow(handle);
        for (var pass = 0; pass < MaxPlacementPasses; pass++)
        {
            if (!GetWindowRect(handle, out var bounds))
            {
                return;
            }

            var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            var location = TrayMenuPlacement.Locate(_anchor, size, _monitor);
            SetWindowPos(handle, HwndTopmost, location.X, location.Y, 0, 0, SwpNoSize | SwpNoActivate);

            var settled = GetDpiForWindow(handle);
            if (settled == dpi)
            {
                return;
            }

            dpi = settled;
        }
    }

    /// <summary>
    /// The hidden window and id the icon is registered with the shell under. WinForms keeps them
    /// private, so they are read by reflection; null when that fails, and the caller falls back.
    /// </summary>
    private (IntPtr Handle, uint Id)? IconWindow()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(NotifyIcon);
        var window = (type.GetField("_window", Private) ?? type.GetField("window", Private))?.GetValue(_icon) as NativeWindow;
        if (window is null || window.Handle == IntPtr.Zero)
        {
            return null;
        }

        return (type.GetField("_id", Private) ?? type.GetField("id", Private))?.GetValue(_icon) switch
        {
            uint id => (window.Handle, id),
            int id => (window.Handle, (uint)id),
            _ => null
        };
    }

    private static bool TryGetIconRect(IntPtr window, uint id, out Rect rect)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = window,
            Id = id
        };

        return Shell_NotifyIconGetRect(ref identifier, out rect) == 0;
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
