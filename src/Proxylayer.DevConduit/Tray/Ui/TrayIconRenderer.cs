using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// Draws the tray icon in code, one per status colour, so the app ships no image files: a
/// coloured disc with a white forward chevron. Icons are drawn once and kept until disposed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayIconRenderer : IDisposable
{
    private readonly Dictionary<TrayStatusKind, Icon> _icons = [];

    public Icon For(TrayStatusKind kind)
    {
        if (!_icons.TryGetValue(kind, out var icon))
        {
            icon = Draw(ColourOf(kind), SystemInformation.SmallIconSize.Width);
            _icons[kind] = icon;
        }

        return icon;
    }

    public static Color ColourOf(TrayStatusKind kind) => kind switch
    {
        TrayStatusKind.Running => Color.FromArgb(0x2E, 0x9E, 0x44),
        TrayStatusKind.Busy or TrayStatusKind.Attention => Color.FromArgb(0xE0, 0x9A, 0x16),
        TrayStatusKind.Failed => Color.FromArgb(0xD1, 0x34, 0x38),
        _ => Color.FromArgb(0x8A, 0x8A, 0x8A)
    };

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    private static Icon Draw(Color colour, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var inset = Math.Max(1f, size / 16f);
            var disc = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            using (var fill = new SolidBrush(colour))
            {
                graphics.FillEllipse(fill, disc);
            }

            using (var rim = new Pen(ControlPaint.Dark(colour, 0.2f), Math.Max(1f, size / 16f)))
            {
                graphics.DrawEllipse(rim, disc);
            }

            var unit = size / 16f;
            using var chevron = new Pen(Color.White, 2.2f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            graphics.DrawLines(chevron, [new PointF(6.2f * unit, 4.6f * unit), new PointF(10f * unit, 8f * unit), new PointF(6.2f * unit, 11.4f * unit)]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
