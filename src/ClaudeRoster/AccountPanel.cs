using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeRoster;

internal abstract record PanelItem;

/// <summary>A small all-caps heading, or a one-line note when <paramref name="IsNote"/>.</summary>
internal sealed record PanelHeader(string Text, bool IsNote = false) : PanelItem;

internal sealed record PanelSeparator : PanelItem;

/// <param name="Detail">The line under the name, e.g. "Signed in · updated 2 min ago".</param>
/// <param name="SessionLeft">Percent of the 5-hour limit left, when known.</param>
/// <param name="WeeklyLeft">Percent of the 7-day limit left, when known.</param>
/// <param name="More">Rename / remove and the like, shown behind a ⋯ button.</param>
internal sealed record PanelAccount(
    string Name,
    string Detail,
    int? SessionLeft,
    int? WeeklyLeft,
    bool Active,
    bool Enabled,
    Action OnClick,
    IReadOnlyList<PanelItem> More) : PanelItem;

internal sealed record PanelAction(string Glyph, string Text, bool Enabled, Action OnClick, bool Danger = false) : PanelItem;

/// <summary>A setting with a switch. The panel stays open when it's flipped.</summary>
internal sealed record PanelToggle(string Text, bool On, bool Enabled, Action<bool> OnChange) : PanelItem;

/// <summary>
/// The account switcher: a dark, rounded popup in the style of Claude's own menus. It closes when it loses focus,
/// on Escape, or when an item is chosen; the chosen action runs after it's gone so dialogs open on their own.
/// </summary>
internal sealed class AccountPanel : Form
{
    private const int Width96 = 330;
    private const int Pad96 = 6;
    private const string MoreGlyph = "";
    private const string CheckGlyph = "";

    private readonly Func<IReadOnlyList<PanelItem>> _build;
    private readonly AccountPanel? _parent;
    private readonly List<(PanelItem Item, Rectangle Bounds)> _rows = [];
    private IReadOnlyList<PanelItem> _items;
    private float _scale = 1;
    private Palette _palette = Palette.Dark;
    private Fonts? _fonts;
    private int _hover = -1;
    private bool _hoverMore;
    private AccountPanel? _child;
    private bool _closing;

    /// <param name="build">Makes the items; called again after a switch is flipped so the panel stays current.</param>
    public AccountPanel(Func<IReadOnlyList<PanelItem>> build) : this(build, parent: null)
    {
    }

    private AccountPanel(Func<IReadOnlyList<PanelItem>> build, AccountPanel? parent)
    {
        _build = build;
        _parent = parent;
        _items = build();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        TopMost = true;
        Text = "Claude Roster";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no taskbar button or Alt+Tab entry.
            parameters.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return parameters;
        }
    }

    /// <summary>Opens just below <paramref name="anchor"/> (Claude's title bar button), left edges lined up.</summary>
    public void ShowBelow(Rectangle anchor) => Open(anchor.Location, size => new Point(anchor.Left, anchor.Bottom + Px(6)));

    /// <summary>Opens above and to the left of a point, as a tray flyout does.</summary>
    public void ShowAbove(Point point) => Open(point, size => new Point(point.X - size.Width + Px(12), point.Y - size.Height - Px(12)));

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
        var border = ColorRef(_palette.Border);
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        // Laid out for the monitor it opens on; don't let WinForms rescale it.
        if (m.Msg == 0x02E0)
        {
            m.Result = 0;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Focus may only be moving to the ⋯ submenu or back; decide once it has landed.
        SynchronizationContext.Current?.Post(_ =>
        {
            var root = Root;
            if (!_closing && !root._closing && !root.IsDisposed && !IsFamilyActive())
            {
                root.CloseAll();
            }
        }, null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            if (_parent is null)
            {
                CloseAll();
            }
            else
            {
                Close();
                _parent.Activate();
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _child?.Close();
        _fonts?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = _rows.FindIndex(row => row.Bounds.Contains(e.Location) && IsInteractive(row.Item));
        var hoverMore = hover >= 0 && _rows[hover].Item is PanelAccount { More.Count: > 0 } && MoreBounds(_rows[hover].Bounds).Contains(e.Location);
        if (hover != _hover || hoverMore != _hoverMore)
        {
            _hover = hover;
            _hoverMore = hoverMore;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _hoverMore = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var index = _rows.FindIndex(row => row.Bounds.Contains(e.Location) && IsInteractive(row.Item));
        if (index < 0)
        {
            return;
        }

        var (item, bounds) = _rows[index];
        switch (item)
        {
            case PanelAccount { More.Count: > 0 } account when MoreBounds(bounds).Contains(e.Location):
                OpenChild(account.More, RectangleToScreen(MoreBounds(bounds)));
                break;
            case PanelAccount { Enabled: true } account:
                Choose(account.OnClick);
                break;
            case PanelAction action:
                Choose(action.OnClick);
                break;
            case PanelToggle toggle:
                toggle.OnChange(!toggle.On);
                // Turning sharing on asks first, and that dialog can close the panel.
                if (!Root.IsDisposed)
                {
                    Root.Rebuild();
                }
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(_palette.Back);
        if (_fonts is null)
        {
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < _rows.Count; i++)
        {
            var (item, bounds) = _rows[i];
            var hovered = i == _hover;
            if (hovered && !(item is PanelAccount && _hoverMore))
            {
                FillRound(graphics, bounds, Px(6), _palette.Hover);
            }

            switch (item)
            {
                case PanelHeader header:
                    PaintHeader(graphics, header, bounds);
                    break;
                case PanelSeparator:
                    using (var line = new Pen(_palette.Border))
                    {
                        var y = bounds.Top + bounds.Height / 2;
                        graphics.DrawLine(line, bounds.Left + Px(6), y, bounds.Right - Px(6), y);
                    }

                    break;
                case PanelAccount account:
                    PaintAccount(graphics, account, bounds, hovered);
                    break;
                case PanelAction action:
                    PaintAction(graphics, action, bounds);
                    break;
                case PanelToggle toggle:
                    PaintToggle(graphics, toggle, bounds);
                    break;
            }
        }
    }

    private AccountPanel Root => _parent?.Root ?? this;

    private void Open(Point near, Func<Size, Point> place)
    {
        _scale = MonitorScale(near);
        _palette = AppsUseLightTheme() ? Palette.Light : Palette.Dark;
        _fonts?.Dispose();
        _fonts = new Fonts(_scale);
        Arrange(out var size);
        var location = place(size);
        var area = Screen.FromPoint(near).WorkingArea;
        location.X = Math.Clamp(location.X, area.Left + Px(4), Math.Max(area.Left + Px(4), area.Right - size.Width - Px(4)));
        location.Y = Math.Clamp(location.Y, area.Top + Px(4), Math.Max(area.Top + Px(4), area.Bottom - size.Height - Px(4)));
        Bounds = new Rectangle(location, size);
        Show();
        SetForegroundWindow(Handle);
        Activate();
    }

    private void Rebuild()
    {
        _child?.Close();
        _items = _build();
        Arrange(out var size);
        Size = size;
        Invalidate();
    }

    private void Arrange(out Size size)
    {
        _rows.Clear();
        var width = Px(Width96);
        var y = Px(Pad96);
        foreach (var item in _items)
        {
            var height = item switch
            {
                PanelHeader { IsNote: true } => Px(30),
                PanelHeader => Px(28),
                PanelSeparator => Px(9),
                PanelAccount account => Px(account.SessionLeft is null && account.WeeklyLeft is null ? 54 : 90),
                _ => Px(36),
            };
            _rows.Add((item, new Rectangle(Px(Pad96), y, width - Px(Pad96) * 2, height)));
            y += height;
        }

        size = new Size(width, y + Px(Pad96));
        _hover = -1;
    }

    private void OpenChild(IReadOnlyList<PanelItem> items, Rectangle anchor)
    {
        _child?.Close();
        _child = new AccountPanel(() => items, this);
        _child.FormClosed += (_, _) => _child = null;
        _child.Open(anchor.Location, size => new Point(anchor.Right - size.Width, anchor.Bottom + Px(4)));
    }

    /// <summary>Closes the panel first, so a confirmation or name dialog opens on its own.</summary>
    private void Choose(Action action)
    {
        var root = Root;
        root.CloseAll();
        var ui = SynchronizationContext.Current;
        if (ui is null)
        {
            action();
        }
        else
        {
            ui.Post(_ => action(), null);
        }
    }

    private void CloseAll()
    {
        _closing = true;
        _child?.CloseAll();
        Close();
    }

    private bool IsFamilyActive()
    {
        var active = GetForegroundWindow();
        for (var panel = Root; panel is not null; panel = panel._child)
        {
            if (panel.IsHandleCreated && panel.Handle == active)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInteractive(PanelItem item) => item switch
    {
        PanelAccount account => account.Enabled || account.More.Count > 0,
        PanelAction action => action.Enabled,
        PanelToggle toggle => toggle.Enabled,
        _ => false,
    };

    private Rectangle MoreBounds(Rectangle row) => new(row.Right - Px(34), row.Top + Px(11), Px(28), Px(28));

    private void PaintHeader(Graphics graphics, PanelHeader header, Rectangle bounds)
    {
        var text = Rectangle.FromLTRB(bounds.Left + Px(10), bounds.Top, bounds.Right - Px(10), bounds.Bottom);
        if (header.IsNote)
        {
            DrawText(graphics, header.Text, _fonts!.Body, text, _palette.Dim);
        }
        else
        {
            DrawText(graphics, header.Text.ToUpperInvariant(), _fonts!.Caption, text, _palette.Dim);
        }
    }

    private void PaintAccount(Graphics graphics, PanelAccount account, Rectangle bounds, bool hovered)
    {
        var fonts = _fonts!;
        var text = account.Enabled || account.Active ? _palette.Text : _palette.Dim;

        // Avatar: initials on a colour picked from the name, ringed in the accent when signed in.
        var avatar = new Rectangle(bounds.Left + Px(8), bounds.Top + Px(10), Px(34), Px(34));
        using (var fill = new SolidBrush(AvatarColor(account.Name)))
        {
            graphics.FillEllipse(fill, avatar);
        }

        if (account.Active)
        {
            using var ring = new Pen(_palette.Accent, Px(2));
            var outer = avatar;
            outer.Inflate(Px(2), Px(2));
            graphics.DrawEllipse(ring, outer);
        }

        TextRenderer.DrawText(graphics, Initials(account.Name), fonts.Initials, avatar, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        var left = avatar.Right + Px(12);
        var right = bounds.Right - Px(40);
        DrawText(graphics, account.Name, fonts.Title, Rectangle.FromLTRB(left, bounds.Top + Px(8), right, bounds.Top + Px(28)), text);
        DrawText(graphics, account.Detail, fonts.Small, Rectangle.FromLTRB(left, bounds.Top + Px(28), right, bounds.Top + Px(46)),
            account.Active ? _palette.Accent : _palette.Dim);

        var more = MoreBounds(bounds);
        if (account.More.Count > 0 && hovered)
        {
            if (_hoverMore)
            {
                FillRound(graphics, more, Px(6), _palette.Hover);
            }

            DrawGlyph(graphics, MoreGlyph, fonts.Glyph, more, _palette.Dim);
        }
        else if (account.Active)
        {
            DrawGlyph(graphics, CheckGlyph, fonts.Glyph, more, _palette.Accent);
        }

        var line = bounds.Top + Px(52);
        foreach (var (label, left100) in new[] { ("Session", account.SessionLeft), ("Week", account.WeeklyLeft) })
        {
            if (left100 is null)
            {
                continue;
            }

            PaintMeter(graphics, label, left100.Value, Rectangle.FromLTRB(left, line, bounds.Right - Px(12), line + Px(16)));
            line += Px(18);
        }
    }

    /// <summary>"Session ▬▬▬▬▬▬▭▭ 93% left": what's left of a limit, coloured as it runs low.</summary>
    private void PaintMeter(Graphics graphics, string label, int left, Rectangle bounds)
    {
        var fonts = _fonts!;
        var labelWidth = Px(50);
        var valueWidth = Px(58);
        DrawText(graphics, label, fonts.Small, new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height), _palette.Dim);
        DrawText(graphics, $"{left}% left", fonts.Small,
            new Rectangle(bounds.Right - valueWidth, bounds.Top, valueWidth, bounds.Height), _palette.Text, TextFormatFlags.Right);

        var track = Rectangle.FromLTRB(bounds.Left + labelWidth, bounds.Top + bounds.Height / 2 - Px(2), bounds.Right - valueWidth - Px(6), bounds.Top + bounds.Height / 2 + Px(2));
        FillRound(graphics, track, track.Height / 2, _palette.Track);
        if (left > 0)
        {
            var filled = track with { Width = Math.Max(track.Height, track.Width * left / 100) };
            FillRound(graphics, filled, track.Height / 2, left > 25 ? _palette.Accent : left > 10 ? _palette.Warning : _palette.Danger);
        }
    }

    private void PaintAction(Graphics graphics, PanelAction action, Rectangle bounds)
    {
        var color = !action.Enabled ? _palette.Disabled : action.Danger ? _palette.Danger : _palette.Text;
        DrawGlyph(graphics, action.Glyph, _fonts!.Glyph, new Rectangle(bounds.Left + Px(8), bounds.Top, Px(20), bounds.Height), color);
        DrawText(graphics, action.Text, _fonts.Body, Rectangle.FromLTRB(bounds.Left + Px(38), bounds.Top, bounds.Right - Px(8), bounds.Bottom), color);
    }

    private void PaintToggle(Graphics graphics, PanelToggle toggle, Rectangle bounds)
    {
        var color = toggle.Enabled ? _palette.Text : _palette.Disabled;
        DrawText(graphics, toggle.Text, _fonts!.Body, Rectangle.FromLTRB(bounds.Left + Px(10), bounds.Top, bounds.Right - Px(56), bounds.Bottom), color);

        var track = new Rectangle(bounds.Right - Px(46), bounds.Top + (bounds.Height - Px(18)) / 2, Px(34), Px(18));
        FillRound(graphics, track, track.Height / 2, toggle.On ? _palette.Accent : _palette.Track);
        var knob = Px(12);
        var x = toggle.On ? track.Right - knob - Px(3) : track.Left + Px(3);
        using var brush = new SolidBrush(toggle.On ? Color.White : _palette.Dim);
        graphics.FillEllipse(brush, x, track.Top + Px(3), knob, knob);
    }

    private static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color, TextFormatFlags extra = 0) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | extra);

    private static void DrawGlyph(Graphics graphics, string glyph, Font font, Rectangle bounds, Color color) =>
        TextRenderer.DrawText(graphics, glyph, font, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

    private static void FillRound(Graphics graphics, Rectangle bounds, int radius, Color color)
    {
        using var brush = new SolidBrush(color);
        if (radius <= 0 || bounds.Width < radius * 2 || bounds.Height < radius * 2)
        {
            graphics.FillRectangle(brush, bounds);
            return;
        }

        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    internal static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)],
            _ => $"{words[0][0]}{words[^1][0]}",
        };
        return initials.ToUpperInvariant();
    }

    private static Color AvatarColor(string name)
    {
        Color[] colors =
        [
            Color.FromArgb(0xC1, 0x5F, 0x3C), Color.FromArgb(0x3E, 0x7C, 0xB1), Color.FromArgb(0x5B, 0x8C, 0x5A),
            Color.FromArgb(0x8E, 0x5B, 0xA8), Color.FromArgb(0xB0, 0x86, 0x2E), Color.FromArgb(0x2F, 0x8C, 0x8C),
        ];
        var hash = name.Aggregate(17, (sum, c) => unchecked(sum * 31 + char.ToUpperInvariant(c)));
        return colors[(hash & int.MaxValue) % colors.Length];
    }

    private int Px(int pixels96) => (int)Math.Round(pixels96 * _scale);

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static float MonitorScale(Point point)
    {
        var monitor = MonitorFromPoint(point, 2); // MONITOR_DEFAULTTONEAREST
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96f : 1;
    }

    private static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not 0;
    }

    private sealed class Fonts(float scale) : IDisposable
    {
        public Font Title { get; } = new("Segoe UI Semibold", 14 * scale, GraphicsUnit.Pixel);
        public Font Body { get; } = new("Segoe UI", 14 * scale, GraphicsUnit.Pixel);
        public Font Small { get; } = new("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        public Font Caption { get; } = new("Segoe UI Semibold", 11 * scale, GraphicsUnit.Pixel);
        public Font Initials { get; } = new("Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);
        public Font Glyph { get; } = GlyphFont(14 * scale);

        public void Dispose()
        {
            Title.Dispose();
            Body.Dispose();
            Small.Dispose();
            Caption.Dispose();
            Initials.Dispose();
            Glyph.Dispose();
        }

        /// <summary>Windows 11's icon font, or the Windows 10 one it replaced (same code points).</summary>
        private static Font GlyphFont(float pixels)
        {
            var font = new Font("Segoe Fluent Icons", pixels, GraphicsUnit.Pixel);
            if (font.Name == "Segoe Fluent Icons")
            {
                return font;
            }

            font.Dispose();
            return new Font("Segoe MDL2 Assets", pixels, GraphicsUnit.Pixel);
        }
    }

    /// <summary>Close to Claude's own menus; the accent is Claude's clay orange.</summary>
    private sealed record Palette(Color Back, Color Hover, Color Border, Color Text, Color Dim, Color Disabled, Color Track, Color Accent, Color Warning, Color Danger)
    {
        public static readonly Palette Dark = new(
            Back: Color.FromArgb(0x24, 0x24, 0x24),
            Hover: Color.FromArgb(0x33, 0x33, 0x33),
            Border: Color.FromArgb(0x3A, 0x3A, 0x3A),
            Text: Color.FromArgb(0xEC, 0xEB, 0xE8),
            Dim: Color.FromArgb(0x9C, 0x9A, 0x96),
            Disabled: Color.FromArgb(0x66, 0x65, 0x63),
            Track: Color.FromArgb(0x3D, 0x3D, 0x3D),
            Accent: Color.FromArgb(0xD9, 0x77, 0x57),
            Warning: Color.FromArgb(0xE0, 0xA3, 0x3A),
            Danger: Color.FromArgb(0xE5, 0x5B, 0x5B));

        public static readonly Palette Light = new(
            Back: Color.FromArgb(0xFD, 0xFC, 0xF9),
            Hover: Color.FromArgb(0xF0, 0xEE, 0xE8),
            Border: Color.FromArgb(0xDE, 0xDC, 0xD4),
            Text: Color.FromArgb(0x29, 0x28, 0x26),
            Dim: Color.FromArgb(0x73, 0x71, 0x6C),
            Disabled: Color.FromArgb(0xAD, 0xAB, 0xA6),
            Track: Color.FromArgb(0xE6, 0xE4, 0xDD),
            Accent: Color.FromArgb(0xC9, 0x64, 0x42),
            Warning: Color.FromArgb(0xB7, 0x7E, 0x16),
            Danger: Color.FromArgb(0xC8, 0x3E, 0x3E));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
