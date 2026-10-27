using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeRoster.Core;
using Microsoft.Win32;

namespace ClaudeRoster;

internal static class Dialogs
{
    private const string Title = "Claude Roster";

    public static bool Confirm(string text) =>
        Show(text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    public static void Info(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Error(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>A tray app has no window, so give each box a topmost owner or it can open behind everything.</summary>
    private static DialogResult Show(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
        };
        owner.Show();
        return MessageBox.Show(owner, text, Title, buttons, icon);
    }
}

internal sealed class AliasDialog : Form
{
    private readonly TextBox _input;

    internal AliasDialog(string title, string prompt, string initial)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        var label = new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 0, 0, 8) };
        _input = new TextBox { Text = initial, Width = 360, MaxLength = AccountSwitcher.MaxAliasLength };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([cancel, save]);
        layout.Controls.AddRange([label, _input, buttons]);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            Activate();
            _input.Focus();
            _input.SelectAll();
        };
    }

    public static string? Ask(string title, string prompt, string initial)
    {
        using var dialog = new AliasDialog(title, prompt, initial);
        return dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog._input.Text)
            ? dialog._input.Text.Trim()
            : null;
    }
}

internal static class StartupRegistration
{
    /// <summary>Passed by the sign-in entry: start hidden and only show the icon while Claude is open.</summary>
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKey = @"Software\ClaudeRoster";
    private const string ValueName = "ClaudeRoster";
    private const string DefaultAppliedName = "StartWithWindowsDefaultApplied";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>For uninstalling: drops the startup entry and every setting Claude Roster keeps in the registry.</summary>
    public static void Forget()
    {
        SetEnabled(false);
        Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// Turns Start with Windows on the first time Claude Roster runs; after that the user's choice stands.
    /// An enabled entry is re-pointed at this copy of the exe, in case it has moved.
    /// </summary>
    /// <returns>True when this call turned it on.</returns>
    public static bool ApplyDefault()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (settings.GetValue(DefaultAppliedName) is null)
        {
            settings.SetValue(DefaultAppliedName, 1, RegistryValueKind.DWord);
            SetEnabled(true);
            return true;
        }

        if (IsEnabled())
        {
            SetEnabled(true);
        }

        return false;
    }
}

/// <summary>Drawn at runtime (two arrows on a rounded tile) so the project ships no binary assets.</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bitmap = Draw(32);
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

    /// <summary>The icon artwork at any size; it's laid out on a 32 px grid and scaled.</summary>
    internal static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size / 32f, size / 32f);

        using var tile = new GraphicsPath();
        const int radius = 8;
        var bounds = new Rectangle(1, 1, 30, 30);
        tile.AddArc(bounds.X, bounds.Y, radius * 2, radius * 2, 180, 90);
        tile.AddArc(bounds.Right - radius * 2, bounds.Y, radius * 2, radius * 2, 270, 90);
        tile.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        tile.AddArc(bounds.X, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        tile.CloseFigure();
        using var fill = new SolidBrush(Color.FromArgb(0x2F, 0x5D, 0x7C));
        graphics.FillPath(fill, tile);

        using var arrow = new Pen(Color.White, 3f) { StartCap = LineCap.Round };
        using var head = new AdjustableArrowCap(2.2f, 2.2f);
        arrow.CustomEndCap = head;
        graphics.DrawLine(arrow, 8, 11, 23, 11);
        graphics.DrawLine(arrow, 24, 21, 9, 21);
        return bitmap;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
