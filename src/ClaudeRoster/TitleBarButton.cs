using System.ComponentModel;
using System.Runtime.InteropServices;
using ClaudeRoster.Core;
using Microsoft.Win32;

namespace ClaudeRoster;

/// <param name="Detail">Shown dimmer after the name, e.g. the session usage left.</param>
/// <param name="Tip">The tooltip, e.g. the full usage summary.</param>
internal sealed record TitleBarLabel(string Text, string? Detail, string Tip);

/// <summary>
/// The account button pinned to the top left of Claude's window, beside its back and forward arrows. It follows the
/// window as it moves, sits just above it in the z-order (not over other apps), and opens the account panel.
/// </summary>
internal sealed class TitleBarButton : Form
{
    // Laid out in 96-DPI pixels against Claude's title bar: its icons end near x 130 and its buttons are 28 high at y 4.
    private const int Left96 = 142;
    private const int Top96 = 4;
    private const int Height96 = 28;
    private const int MaxWidth96 = 190;
    private const int Padding96 = 9;
    private const int Gap96 = 6;
    private const int Icon96 = 14;
    private const int Chevron96 = 10;

    private const string ContactGlyph = "";
    private const string ChevronGlyph = "";

    private readonly ClaudeProcess _claude;
    private readonly Action<Rectangle> _onClick;
    private readonly Func<TitleBarLabel> _label;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };
    private readonly ToolTip _tip = new();
    private readonly WinEventProc _onWinEvent;
    private readonly List<nint> _windowHooks = [];
    private nint _systemHook;
    private nint _target;
    private bool _enabled;
    private bool _hover;
    private bool _roundedByDwm;
    private bool _light;
    private float _scale;
    private Font? _textFont;
    private Font? _iconFont;
    private Font? _chevronFont;
    private TitleBarLabel _text = new("", null, "");

    /// <param name="onClick">Opens the account panel under the button (given in screen coordinates).</param>
    /// <param name="label">What the button says: the signed-in account. Polled, so it must be cheap.</param>
    public TitleBarButton(ClaudeProcess claude, Action<Rectangle> onClick, Func<TitleBarLabel> label)
    {
        _claude = claude;
        _onClick = onClick;
        _label = label;
        _onWinEvent = OnWinEvent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Text = "Claude Roster";
        _watch.Tick += (_, _) => Poll();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindowInt;
            return parameters;
        }
    }

    /// <summary>Turns the button on or off; while on it watches for Claude's window and follows it.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _systemHook = SetWinEventHook(EventSystemForeground, EventSystemMinimizeEnd, 0, _onWinEvent, 0, 0, WinEventOutOfContext);
            _watch.Start();
            Poll();
        }
        else
        {
            _watch.Stop();
            Unhook(ref _systemHook);
            Track(0);
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SetEnabled(false);
            _watch.Dispose();
            _tip.Dispose();
            _textFont?.Dispose();
            _iconFont?.Dispose();
            _chevronFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounds the corners like Claude's own buttons; Windows 10 keeps them square and we draw a border.
        var corner = DwmCornerRoundSmall;
        _roundedByDwm = DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref corner, sizeof(int)) == 0;
        ApplyBorderColor();
    }

    protected override void WndProc(ref Message m)
    {
        // The button sizes itself from Claude's window DPI; don't let WinForms rescale it when it crosses monitors.
        if (m.Msg == WmDpiChanged)
        {
            m.Result = 0;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            _onClick(Bounds);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = _light ? Palette.Light : Palette.Dark;
        var graphics = e.Graphics;
        graphics.Clear(_hover ? palette.Hover : palette.Back);
        if (!_roundedByDwm)
        {
            using var border = new Pen(palette.Border);
            graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        if (_textFont is null || _iconFont is null || _chevronFont is null)
        {
            return;
        }

        const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        var icon = new Rectangle(Px(Padding96), 0, Px(Icon96), Height);
        var chevron = new Rectangle(Width - Px(Padding96) - Px(Chevron96), 0, Px(Chevron96), Height);
        var text = Rectangle.FromLTRB(icon.Right + Px(Gap96), 0, chevron.Left - Px(Gap96), Height);
        TextRenderer.DrawText(graphics, ContactGlyph, _iconFont, icon, palette.Text, centered);
        TextRenderer.DrawText(graphics, ChevronGlyph, _chevronFont, chevron, palette.Dim, centered);
        const TextFormatFlags line = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (_text.Detail is { } detail)
        {
            // The detail (usage left) always shows; the name gives way to it when space runs out.
            var detailWidth = Math.Min(TextRenderer.MeasureText(detail, _textFont, Size.Empty, line).Width, text.Width / 2);
            var detailBounds = Rectangle.FromLTRB(text.Right - detailWidth, 0, text.Right, Height);
            TextRenderer.DrawText(graphics, detail, _textFont, detailBounds, palette.Dim, line | TextFormatFlags.Right);
            text = Rectangle.FromLTRB(text.Left, 0, detailBounds.Left - Px(Gap96), Height);
        }

        TextRenderer.DrawText(graphics, _text.Text, _textFont, text, palette.Text, line | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Picks up Claude's window if it came or went, the account shown, and the colours, then repositions.</summary>
    private void Poll()
    {
        if (!_enabled)
        {
            return;
        }

        if (_target == 0 || !IsWindow(_target) || !IsWindowVisible(_target))
        {
            Track(FindClaudeWindow());
        }

        var light = AppsUseLightTheme();
        var text = _label();
        if (text != _text || light != _light)
        {
            _text = text;
            _light = light;
            _tip.SetToolTip(this, text.Tip);
            ApplyBorderColor();
            Invalidate();
        }

        Follow();
        if (Visible && !IsJustAboveClaude())
        {
            Raise();
        }
    }

    /// <summary>Moves the button onto Claude's title bar, or hides it while the window is closed, minimised or on another desktop.</summary>
    private void Follow()
    {
        if (!_enabled || !IsShowing(_target))
        {
            Hide();
            return;
        }

        var scale = GetDpiForWindow(_target) / 96f;
        if (scale != _scale)
        {
            _scale = scale > 0 ? scale : 1;
            CreateFonts();
        }

        if (DwmGetWindowAttribute(_target, DwmExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(_target, out frame))
        {
            Hide();
            return;
        }

        var bounds = new Rectangle(frame.Left + Px(Left96), frame.Top + Px(Top96), MeasureWidth(), Px(Height96));
        if (Visible && IsCloaked(Handle))
        {
            // Claude was moved to another virtual desktop; a fresh window opens on the one in view, where Claude is.
            Hide();
            RecreateHandle();
        }

        if (Bounds != bounds)
        {
            SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoZOrder | SwpNoActivate);
        }

        if (!Visible)
        {
            Show();
            Raise();
        }
    }

    /// <summary>Puts the button directly above Claude's window, so other apps still cover both.</summary>
    private void Raise()
    {
        var above = GetWindow(_target, GwHwndPrev);
        if (above == Handle)
        {
            return;
        }

        var topmost = above != 0 && (GetWindowLongPtrW(above, GwlExStyle) & WsExTopmost) != 0;
        SetWindowPos(Handle, above == 0 || topmost ? HwndTop : above, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// False when something other than Claude sits right under the button, e.g. Claude was brought forward without
    /// becoming the active window and now covers it.
    /// </summary>
    private bool IsJustAboveClaude()
    {
        for (var below = GetWindow(Handle, GwHwndNext); below != 0; below = GetWindow(below, GwHwndNext))
        {
            if (below == _target)
            {
                return true;
            }

            if (IsWindowVisible(below) && !IsCloaked(below))
            {
                return false;
            }
        }

        return false;
    }

    private void OnWinEvent(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (window != _target || window == 0 || objectId != ObjIdWindow)
        {
            return;
        }

        if (type == EventObjectDestroy)
        {
            Track(0);
            Hide();
            return;
        }

        Follow();
        if (type == EventSystemForeground && Visible)
        {
            Raise();
        }
    }

    /// <summary>Hooks the moves, shows, hides and cloaking of one Claude window (all in Claude's process).</summary>
    private void Track(nint window)
    {
        if (window == _target)
        {
            return;
        }

        foreach (var hook in _windowHooks)
        {
            UnhookWinEvent(hook);
        }

        _windowHooks.Clear();
        _target = window;
        if (window == 0)
        {
            return;
        }

        GetWindowThreadProcessId(window, out var process);
        foreach (var (first, last) in new[]
                 {
                     (EventObjectDestroy, EventObjectHide),
                     (EventObjectLocationChange, EventObjectLocationChange),
                     (EventObjectCloaked, EventObjectUncloaked),
                 })
        {
            var hook = SetWinEventHook(first, last, 0, _onWinEvent, process, 0, WinEventOutOfContext);
            if (hook != 0)
            {
                _windowHooks.Add(hook);
            }
        }
    }

    /// <summary>Claude's main window: the largest visible, titled, unowned window of the desktop app's processes.</summary>
    private nint FindClaudeWindow()
    {
        IReadOnlyList<int> processes;
        try
        {
            processes = _claude.DesktopProcessIds();
        }
        catch (Win32Exception)
        {
            // Listing processes can fail when Windows is low on memory; look again on the next tick.
            return 0;
        }

        if (processes.Count == 0)
        {
            return 0;
        }

        nint best = 0;
        long bestArea = -1;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (!processes.Contains((int)process)
                || !IsWindowVisible(window)
                || GetWindow(window, GwOwner) != 0
                || (GetWindowLongPtrW(window, GwlExStyle) & WsExToolWindow) != 0
                || GetWindowTextLengthW(window) == 0
                || !GetWindowRect(window, out var rect))
            {
                return true;
            }

            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                best = window;
                bestArea = area;
            }

            return true;
        }, 0);
        return best;
    }

    private int MeasureWidth()
    {
        int Measure(string? value) => value is null || _textFont is null
            ? 0
            : TextRenderer.MeasureText(value, _textFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

        var text = Measure(_text.Text) + (_text.Detail is null ? 0 : Px(Gap96) + Measure(_text.Detail));
        var width = Px(Padding96) * 2 + Px(Icon96) + Px(Chevron96) + Px(Gap96) * 2 + text;
        return Math.Min(width, Px(MaxWidth96));
    }

    private void CreateFonts()
    {
        _textFont?.Dispose();
        _iconFont?.Dispose();
        _chevronFont?.Dispose();
        _textFont = new Font("Segoe UI", 13 * _scale, GraphicsUnit.Pixel);
        _iconFont = GlyphFont(13 * _scale);
        _chevronFont = GlyphFont(9 * _scale);
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

    private void ApplyBorderColor()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var border = (_light ? Palette.Light : Palette.Dark).Border;
        var colorRef = border.R | (border.G << 8) | (border.B << 16);
        DwmSetWindowAttribute(Handle, DwmBorderColor, ref colorRef, sizeof(int));
    }

    private int Px(int pixels96) => (int)Math.Round(pixels96 * (_scale > 0 ? _scale : 1));

    private static bool IsShowing(nint window) =>
        window != 0 && IsWindow(window) && IsWindowVisible(window) && !IsIconic(window) && !IsCloaked(window);

    private static bool IsCloaked(nint window) =>
        DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Follows the Windows app theme; Claude's own appearance setting isn't readable from outside.</summary>
    private static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not 0;
    }

    private static void Unhook(ref nint hook)
    {
        if (hook != 0)
        {
            UnhookWinEvent(hook);
            hook = 0;
        }
    }

    /// <summary>Matched to Claude's title bar (#111 behind 28 px buttons with a #494949 edge).</summary>
    private sealed record Palette(Color Back, Color Hover, Color Border, Color Text, Color Dim)
    {
        public static readonly Palette Dark = new(
            Color.FromArgb(0x1C, 0x1C, 0x1C), Color.FromArgb(0x34, 0x34, 0x34), Color.FromArgb(0x3A, 0x3A, 0x3A),
            Color.FromArgb(0xCC, 0xC9, 0xC9), Color.FromArgb(0x8A, 0x88, 0x86));

        public static readonly Palette Light = new(
            Color.FromArgb(0xF7, 0xF6, 0xF2), Color.FromArgb(0xEA, 0xE8, 0xE1), Color.FromArgb(0xDA, 0xD8, 0xD0),
            Color.FromArgb(0x3D, 0x3D, 0x3A), Color.FromArgb(0x7A, 0x78, 0x72));
    }

    private const int WmDpiChanged = 0x02E0;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x8;
    private const long WsExToolWindow = 0x80;
    private const int WsExToolWindowInt = 0x80;
    private const uint GwHwndNext = 2;
    private const uint GwHwndPrev = 3;
    private const uint GwOwner = 4;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private static readonly nint HwndTop = 0;
    private const int ObjIdWindow = 0;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint WinEventOutOfContext = 0x0;
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCornerRoundSmall = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate void WinEventProc(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time);

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
