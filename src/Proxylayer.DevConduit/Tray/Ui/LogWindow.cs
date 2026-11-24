using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Proxylayer.DevConduit.Tray.Logging;

namespace Proxylayer.DevConduit.Tray.Ui;

/// <summary>
/// The recent log lines from the tray and the local proxy it hosts, refreshed while open,
/// with copy to the clipboard and clear.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LogWindow : Form
{
    private readonly LogRingBuffer _log;
    private readonly TextBox _text;
    private readonly CheckBox _follow;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private long _shownSequence = -1;
    private int _shownCount = -1;

    public LogWindow(LogRingBuffer log)
    {
        _log = log;

        Text = "Dev.Conduit log";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 600);
        MinimumSize = new Size(500, 300);
        ShowInTaskbar = true;

        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = SystemColors.Window
        };

        _follow = new CheckBox { Text = "Follow new lines", Checked = true, AutoSize = true, Margin = new Padding(3, 7, 12, 3) };

        var copy = new Button { Text = "Copy all", AutoSize = true };
        copy.Click += (_, _) => CopyAll();
        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) => _log.Clear();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange([close, clear, copy, _follow]);

        Controls.Add(_text);
        Controls.Add(buttons);
        CancelButton = close;

        _refresh.Tick += (_, _) => RefreshText();
        Load += (_, _) =>
        {
            RefreshText();
            _refresh.Start();
        };
        FormClosed += (_, _) => _refresh.Dispose();
    }

    private void RefreshText()
    {
        var lines = _log.Snapshot();
        var last = lines.Count == 0 ? 0 : lines[^1].Sequence;
        if (last == _shownSequence && lines.Count == _shownCount)
        {
            return;
        }

        _shownSequence = last;
        _shownCount = lines.Count;

        var selectionStart = _text.SelectionStart;
        var selectionLength = _text.SelectionLength;
        _text.Text = string.Join(Environment.NewLine, lines.Select(line => line.ToString()));

        if (_follow.Checked)
        {
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }
        else
        {
            _text.Select(Math.Min(selectionStart, _text.TextLength), selectionLength);
        }
    }

    private void CopyAll()
    {
        var text = _log.ToText();
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }
}
