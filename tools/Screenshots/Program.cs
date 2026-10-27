using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClaudeRoster.Core;

namespace ClaudeRoster.Screenshots;

/// <summary>
/// Renders the tray app's real menu and dialogs for the README, using two sample accounts in a throwaway data
/// folder and a process controller that does nothing, so the real Claude install is never read or touched.
/// A plain backdrop covers every screen while it runs, so nothing else on the desktop ends up in a capture.
/// </summary>
/// <remarks>Usage: dotnet run --project tools/Screenshots [output folder, default docs/images]</remarks>
internal static class Program
{
    private const string WorkAccount = "11111111-aaaa-4aaa-8aaa-111111111111";
    private const string PersonalAccount = "22222222-bbbb-4bbb-8bbb-222222222222";
    private const int Margin = 28;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("docs", "images"));
        // Not ApplicationConfiguration.Initialize(): the tray app's generated copy is visible here and would clash.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var backdrop = new Backdrop();
        backdrop.Shown += async (_, _) =>
        {
            try
            {
                await CaptureAllAsync(output);
                Console.WriteLine($"Saved screenshots to {output}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            finally
            {
                backdrop.Close();
            }
        };
        Application.Run(backdrop);
        return Environment.ExitCode;
    }

    private static async Task CaptureAllAsync(string output)
    {
        Directory.CreateDirectory(output);
        using (var icon = TrayIcon.Draw(128))
        {
            icon.Save(Path.Combine(output, "icon.png"), ImageFormat.Png);
        }

        using var sandbox = new Sandbox();
        var switcher = sandbox.CreateSwitcher();
        // Built while signed out, so the "save your account" balloon doesn't fire.
        using var tray = new TrayContext(switcher);

        // The first menu a process shows doesn't paint in time to be captured; get that out of the way.
        await Task.Delay(500);
        tray.Menu.Show(Point.Empty);
        await Task.Delay(500);
        tray.Menu.Close();

        // First run: Claude is signed in, nothing saved yet.
        sandbox.SignIn(WorkAccount, "work");
        await CaptureMenuAsync(tray.Menu, Path.Combine(output, "first-run.png"));
        await CaptureFormAsync(
            new AliasDialog(TrayContext.SaveCurrentTitle, TrayContext.SaveCurrentPrompt, "Work"),
            Path.Combine(output, "save-account.png"));
        var work = switcher.SaveCurrent("Work");

        // Add another account.
        await CaptureMessageBoxAsync(
            () => Dialogs.Confirm(TrayContext.AddAccountConfirmation(switcher.GetStatus())),
            Path.Combine(output, "confirm-add.png"));
        await switcher.PrepareNewSignInAsync();
        sandbox.SignIn(PersonalAccount, "personal");
        await CaptureFormAsync(
            new AliasDialog(TrayContext.SaveCurrentTitle, TrayContext.NewAccountPrompt, "Personal"),
            Path.Combine(output, "name-new-account.png"));
        switcher.SaveCurrent("Personal");

        // Everyday use: Work is signed in, Personal is one click away.
        await switcher.SwitchAsync(work.Id);
        var personal = switcher.ListProfiles().First(profile => profile.Alias == "Personal");
        // Readings are matched to whichever account was signed in when Claude took them (to the millisecond).
        // Work's older readings (its organization's) draw the 7-day row; the add-on's latest brings reset times.
        var now = DateTimeOffset.UtcNow;
        sandbox.WriteUsage(
            (now.AddDays(-6), "org-work", 35, 12),
            (now.AddDays(-5), "org-work", 62, 20),
            (now.AddDays(-4), "org-work", 81, 31),
            (now.AddDays(-3), "org-work", 48, 38),
            (now.AddDays(-1), "org-work", 94, 52),
            (personal.LastUsedAt!.Value.AddMilliseconds(1), "org-personal", 64, 22),
            (now.AddMilliseconds(1), "org-work", 18, 41));
        sandbox.WriteAddOnReading(now.AddMilliseconds(2), 23, 41, now.AddHours(2).AddMinutes(45), now.AddDays(3).AddHours(4));
        await CapturePanelAsync(tray, Path.Combine(output, "panel.png"));
        await CaptureMenuAsync(tray.Menu, Path.Combine(output, "menu.png"));
        await CaptureMessageBoxAsync(
            () => Dialogs.Confirm(TrayContext.SwitchConfirmation("Personal", switcher.GetStatus())),
            Path.Combine(output, "confirm-switch.png"));
        await CaptureMenuAsync(tray.Menu, Path.Combine(output, "manage.png"), openManage: true);
    }

    private static async Task CaptureMenuAsync(ContextMenuStrip menu, string path, bool openManage = false)
    {
        // Menus close as soon as another app takes the foreground (a closed message box can hand it away),
        // so take it back and keep the menu open until it has been captured.
        Backdrop.Current!.Activate();
        await Task.Delay(300);
        var area = Screen.PrimaryScreen!.WorkingArea;
        menu.AutoClose = false;
        menu.Show(new Point(area.Left + area.Width / 2 - 200, area.Top + area.Height / 4));
        await Task.Delay(700);
        var bounds = menu.Bounds;
        var submenus = new List<ToolStripDropDown>();

        if (openManage)
        {
            var manage = menu.Items.OfType<ToolStripMenuItem>().First(item => item.Text == "Manage accounts");
            var rename = manage.DropDownItems.OfType<ToolStripMenuItem>().First(item => item.Text == "Rename");
            foreach (var item in new[] { manage, rename })
            {
                item.DropDown.AutoClose = false;
                item.Select();
                item.ShowDropDown();
                await Task.Delay(600);
                submenus.Add(item.DropDown);
                bounds = Rectangle.Union(bounds, item.DropDown.Bounds);
            }
        }

        Capture(bounds, path);
        foreach (var submenu in Enumerable.Reverse(submenus))
        {
            submenu.Close();
        }

        menu.Close();
        menu.AutoClose = true;
        await Task.Delay(300);
    }

    /// <summary>The account panel the title bar button and a left click on the tray icon open.</summary>
    private static async Task CapturePanelAsync(TrayContext tray, string path)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var panel = new AccountPanel(tray.BuildPanel);
        panel.ShowBelow(new Rectangle(area.Left + area.Width / 2 - 160, area.Top + area.Height / 6, 0, 0));
        await Task.Delay(800);
        Capture(panel.Bounds, path);
        panel.Close();
        await Task.Delay(300);
    }

    private static async Task CaptureFormAsync(Form form, string path)
    {
        using (form)
        {
            form.Show();
            await Task.Delay(800);
            Capture(Native.FrameBounds(form.Handle), path);
            form.Close();
        }

        await Task.Delay(300);
    }

    /// <summary>Message boxes are modal, so a watcher captures and dismisses the box while it's up.</summary>
    private static async Task CaptureMessageBoxAsync(Action show, string path)
    {
        var watcher = CaptureOpenMessageBoxAsync(path);
        show();
        await watcher;
        await Task.Delay(300);
    }

    private static async Task CaptureOpenMessageBoxAsync(string path)
    {
        nint box = 0;
        for (var attempt = 0; attempt < 50 && box == 0; attempt++)
        {
            await Task.Delay(100);
            box = Native.FindOwnDialog();
        }

        if (box == 0)
        {
            throw new InvalidOperationException("The message box never appeared.");
        }

        await Task.Delay(600);
        Capture(Native.FrameBounds(box), path);
        Native.Close(box);
    }

    private static void Capture(Rectangle bounds, string path)
    {
        bounds.Inflate(Margin, Margin);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"  {Path.GetFileName(path)}");
    }
}

/// <summary>Covers every screen with a plain gradient so captures only ever show the roster's own UI.</summary>
internal sealed class Backdrop : Form
{
    public static Backdrop? Current { get; private set; }

    public Backdrop()
    {
        Current = this;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            ClientRectangle, Color.FromArgb(0xDC, 0xE6, 0xEF), Color.FromArgb(0xEE, 0xF1, 0xF5), LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}

/// <summary>A throwaway Claude data folder and roster home with sample accounts and no real secrets.</summary>
internal sealed class Sandbox : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeRosterScreenshots", Guid.NewGuid().ToString("N"));

    public Sandbox()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, ClaudeConfig.FileName), """{"locale":"en-US"}""");
    }

    private string DataRoot => Path.Combine(_root, "Claude");

    private string AddOnStore => Path.Combine(_root, "ClaudeCodeStore");

    public AccountSwitcher CreateSwitcher() =>
        new(new ClaudeInstall(DataRoot, null, null), new RosterPaths(Path.Combine(_root, "Roster")), process: new DemoClaudeProcess(),
            addOnStore: AddOnStore);

    /// <summary>A reading as the roster-usage add-on saves it, with reset times.</summary>
    public void WriteAddOnReading(DateTimeOffset at, int sessionUsed, int weeklyUsed, DateTimeOffset sessionResets, DateTimeOffset weeklyResets)
    {
        Directory.CreateDirectory(AddOnStore);
        var latest = new JsonObject
        {
            ["at"] = at.ToUnixTimeMilliseconds(),
            ["limits"] = new JsonArray(
                new JsonObject { ["kind"] = "five_hour", ["percentUsed"] = sessionUsed, ["resetsAt"] = sessionResets.ToString("O") },
                new JsonObject { ["kind"] = "seven_day", ["percentUsed"] = weeklyUsed, ["resetsAt"] = weeklyResets.ToString("O") }),
        };
        File.WriteAllText(Path.Combine(AddOnStore, "roster-usage_claude-roster-000000000000.json"), new JsonObject { ["latest"] = latest }.ToJsonString());
    }

    public void SignIn(string accountUuid, string marker)
    {
        Write("Local State", """{"os_crypt":{"encrypted_key":"sample"}}""");
        Write(@"Network\Cookies", $"sample-{marker}");
        var config = ClaudeConfig.Load(DataRoot);
        config[ClaudeConfig.AccountUuidKey] = accountUuid;
        config["oauth:tokenCache"] = $"sample-{marker}";
        config["bootSidebarPlaceholder"] = new JsonArray();
        ClaudeConfig.Save(DataRoot, config);
    }

    /// <summary>Plan usage readings in Claude's format: percent of the 5-hour and weekly limits used.</summary>
    public void WriteUsage(params (DateTimeOffset At, string Org, int SessionUsed, int WeeklyUsed)[] readings)
    {
        var samples = new JsonArray();
        foreach (var reading in readings)
        {
            samples.Add(new JsonObject
            {
                ["t"] = reading.At.ToUnixTimeMilliseconds(),
                ["org"] = reading.Org,
                ["u"] = new JsonObject { ["fh"] = reading.SessionUsed, ["sd"] = reading.WeeklyUsed },
            });
        }

        Write(PlanUsage.FileName, new JsonObject { ["version"] = 2, ["samples"] = samples }.ToJsonString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}

/// <summary>Pretends Claude is open with one Claude Code session; closing and opening do nothing.</summary>
internal sealed class DemoClaudeProcess : IClaudeProcess
{
    public ClaudeRunState Capture() => new(true, null, null, 1);
    public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OpenAsync(ClaudeRunState previous, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool IsDataRootInUse() => false;
}

internal static class Native
{
    private const uint WmClose = 0x0010;
    private const int ExtendedFrameBounds = 9;

    /// <summary>The window's visible bounds, without the invisible resize border Windows adds around it.</summary>
    public static Rectangle FrameBounds(nint window)
    {
        if (DwmGetWindowAttribute(window, ExtendedFrameBounds, out var rect, Marshal.SizeOf<Rect>()) != 0)
        {
            GetWindowRect(window, out rect);
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>A visible standard dialog (#32770, i.e. a message box) belonging to this process.</summary>
    public static nint FindOwnDialog()
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var className = new char[64];
            var length = GetClassNameW(window, className, className.Length);
            if (owner == Environment.ProcessId && IsWindowVisible(window) && new string(className, 0, length) == "#32770")
            {
                found = window;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    public static void Close(nint window) => PostMessageW(window, WmClose, 0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, char[] name, int length);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
