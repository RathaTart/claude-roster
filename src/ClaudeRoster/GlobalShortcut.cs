using System.Runtime.InteropServices;

namespace ClaudeRoster;

/// <summary>
/// A keyboard shortcut that opens the account panel from anywhere in Windows, through a hidden message-only window.
/// Another app may already hold a combination, so there are a few to choose from, and "automatic" takes the first free.
/// </summary>
internal sealed class GlobalShortcut : NativeWindow, IDisposable
{
    /// <summary>The setting that turns the shortcut off; null means automatic.</summary>
    public const string Off = "Off";

    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>The combinations on offer, in the order "automatic" tries them.</summary>
    public static readonly IReadOnlyList<(string Name, uint Modifiers, uint Key)> Choices =
    [
        ("Ctrl+Alt+R", ModControl | ModAlt, 0x52),
        ("Ctrl+Shift+Alt+R", ModControl | ModShift | ModAlt, 0x52),
        ("Ctrl+Alt+K", ModControl | ModAlt, 0x4B),
        ("Ctrl+Alt+Q", ModControl | ModAlt, 0x51),
    ];

    private const int Id = 1;
    private const int WmHotkey = 0x0312;
    private static readonly nint MessageOnlyParent = -3;

    public GlobalShortcut() => CreateHandle(new CreateParams { Parent = MessageOnlyParent });

    public event Action? Pressed;

    /// <summary>The combination in effect, or null when there's none.</summary>
    public string? Current { get; private set; }

    /// <summary>
    /// Takes the chosen combination: one of <see cref="Choices"/> by name, <see cref="Off"/>, or null for the first
    /// that's free.
    /// </summary>
    /// <returns>The combination now in effect, or null when the one chosen is taken (or none is free).</returns>
    public string? Apply(string? choice)
    {
        if (Current is not null)
        {
            UnregisterHotKey(Handle, Id);
            Current = null;
        }

        if (choice == Off)
        {
            return null;
        }

        foreach (var (name, modifiers, key) in Choices.Where(entry => choice is null || entry.Name == choice))
        {
            if (RegisterHotKey(Handle, Id, modifiers | ModNoRepeat, key))
            {
                Current = name;
                break;
            }
        }

        return Current;
    }

    public void Dispose()
    {
        Apply(Off);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == Id)
        {
            Pressed?.Invoke();
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
