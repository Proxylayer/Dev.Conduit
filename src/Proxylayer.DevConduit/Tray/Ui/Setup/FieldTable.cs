using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Proxylayer.DevConduit.Tray.Ui.Setup;

/// <summary>A two-column form layout: a label on the left, the field on the right, an optional grey hint under it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class FieldTable : TableLayoutPanel
{
    public FieldTable()
    {
        ColumnCount = 2;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Padding = new Padding(0, 0, 0, 6);
        // The labels' column fits the longest label, at whatever DPI the window is on.
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public T Add<T>(string label, T field, string? hint = null)
        where T : Control
    {
        var row = NextRow();
        Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }, 0, row);

        field.Anchor = field is CheckBox or NumericUpDown ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(field, 1, row);

        if (hint is not null)
        {
            Controls.Add(Hint(hint), 1, NextRow());
        }

        return field;
    }

    /// <summary>A line of text across both columns.</summary>
    public Label AddNote(string text, Color? colour = null)
    {
        var note = Hint(text);
        if (colour is { } value)
        {
            note.ForeColor = value;
        }

        var row = NextRow();
        Controls.Add(note, 0, row);
        SetColumnSpan(note, 2);
        return note;
    }

    private static Label Hint(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(3, 0, 3, 6)
        };

    private int NextRow()
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return RowCount++;
    }
}
