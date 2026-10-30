using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RunResize;

/// <summary>Asks for the new size. Round runs pick a diameter; rectangular and oval runs pick width and height.</summary>
internal sealed class SizePickerForm : Form
{
    readonly ComboBox _first;
    readonly ComboBox? _height;

    public SizePickerForm(RunKind kind, IReadOnlyList<SizeOption> sizes, RunSize current)
    {
        bool round = RunSizing.IsRound(kind);
        Text = "Resize Run";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        _first = AddRow(grid, round ? "Diameter" : "Width", sizes, round ? current.Diameter : current.Width);
        if (!round) _height = AddRow(grid, "Height", sizes, current.Height);

        var resize = new Button { Text = "Resize", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(resize);

        // The store wants the privacy policy reachable from inside the app; it's on the help page.
        var help = new LinkLabel { Text = "Help and privacy", AutoSize = true, Anchor = AnchorStyles.Left };
        help.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(App.HelpUrl) { UseShellExecute = true });

        var footer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(help);
        footer.Controls.Add(buttons);
        grid.Controls.Add(footer);
        grid.SetColumnSpan(footer, 2);

        Controls.Add(grid);
        AcceptButton = resize;
        CancelButton = cancel;
    }

    public RunSize Selected => _height is null
        ? RunSize.Round(ValueOf(_first))
        : RunSize.Rect(ValueOf(_first), ValueOf(_height));

    public string SelectedLabel => _height is null
        ? TextOf(_first)
        : $"{TextOf(_first)} × {TextOf(_height)}";

    static ComboBox AddRow(TableLayoutPanel grid, string caption, IReadOnlyList<SizeOption> sizes, double current)
    {
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        box.Items.AddRange(sizes.Cast<object>().ToArray());
        box.SelectedIndex = NearestIndex(sizes, current);
        grid.Controls.Add(box);
        return box;
    }

    /// <summary>Preselects the catalog size closest to the run's current size.</summary>
    static int NearestIndex(IReadOnlyList<SizeOption> sizes, double value)
    {
        int best = 0;
        for (int i = 1; i < sizes.Count; i++)
            if (Math.Abs(sizes[i].Value - value) < Math.Abs(sizes[best].Value - value)) best = i;
        return best;
    }

    static double ValueOf(ComboBox box) => ((SizeOption)box.SelectedItem!).Value;
    static string TextOf(ComboBox box) => ((SizeOption)box.SelectedItem!).Label;
}
