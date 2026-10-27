using System.Diagnostics;
using ClaudeRoster.Core;

namespace ClaudeRoster;

/// <summary>The tray icon and its menu: the whole user interface.</summary>
internal sealed class TrayContext : ApplicationContext
{
    internal const string SaveCurrentTitle = "Save current account";
    internal const string SaveCurrentPrompt = "Name the account that's signed in to Claude now.";
    internal const string NewAccountPrompt = "You've signed in to a new account. What should Claude Roster call it?";

    private const string AppName = "Claude Roster";
    private static readonly TimeSpan SignInWatchLimit = TimeSpan.FromMinutes(20);

    private readonly AccountSwitcher _switcher;
    private readonly ClaudeProcess _claude;
    private readonly bool _startupJustEnabled;
    private readonly Icon _icon = TrayIcon.Create();
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _signInWatch = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _claudeWatch = new() { Interval = 2000 };
    private readonly RegisteredWaitHandle? _showRequestWait;
    private readonly TitleBarButton? _titleBar;
    private AccountPanel? _panel;
    private DateTime _panelClosedAt;
    private bool _hideWhileClaudeClosed;
    private DateTime _signInWatchEnds;
    private bool _busy;
    private bool _prompting;
    private bool _greeted;

    /// <param name="hideWhileClaudeClosed">Started at sign-in: keep the icon out of the tray until Claude opens.</param>
    /// <param name="startupJustEnabled">Start with Windows was just turned on by default; say so once.</param>
    /// <param name="showRequest">Signalled by a second launch, which wants the icon shown.</param>
    /// <param name="titleBarButton">Also offer the menu from a button on Claude's own title bar.</param>
    public TrayContext(
        AccountSwitcher switcher,
        bool hideWhileClaudeClosed = false,
        bool startupJustEnabled = false,
        EventWaitHandle? showRequest = null,
        bool titleBarButton = false)
    {
        _switcher = switcher;
        _claude = new ClaudeProcess(switcher.Install);
        _hideWhileClaudeClosed = hideWhileClaudeClosed;
        _startupJustEnabled = startupJustEnabled;
        _menu.Opening += (_, _) => RebuildMenu();
        _tray = new NotifyIcon { Icon = _icon, Text = AppName, ContextMenuStrip = _menu };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowPanel(panel => panel.ShowAbove(Cursor.Position));
            }
        };
        _signInWatch.Tick += (_, _) => CheckForNewSignIn();
        _claudeWatch.Tick += (_, _) => UpdateVisibility();

        if (showRequest is not null)
        {
            // Creating the menu above installed the WinForms context; marshal the request back onto this thread.
            var ui = SynchronizationContext.Current!;
            _showRequestWait = ThreadPool.RegisterWaitForSingleObject(
                showRequest, (_, _) => ui.Post(_ => OnShowRequested(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        UpdateVisibility();
        if (_hideWhileClaudeClosed)
        {
            _claudeWatch.Start();
        }

        if (titleBarButton)
        {
            _titleBar = new TitleBarButton(_claude, anchor => ShowPanel(panel => panel.ShowBelow(anchor)), TitleBarLabel);
            _titleBar.SetEnabled(SafeTitleBarSetting());
        }
    }

    /// <summary>The tray menu; it rebuilds itself every time it opens. Exposed for the screenshot tool.</summary>
    internal ContextMenuStrip Menu => _menu;

    internal static string SwitchConfirmation(string alias, LiveStatus? status) =>
        $"Switch Claude to {alias}?\n\nClaude will close and reopen.{SessionWarning(status)}";

    internal static string AddAccountConfirmation(LiveStatus? status) =>
        "Claude will close and reopen on its sign-in screen. Sign in with the other account, and Claude Roster will ask you to name it.\n\n" +
        "Your current account stays saved. Don't use Log out in Claude for a saved account: that ends its saved sign-in too." +
        SessionWarning(status);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showRequestWait?.Unregister(null);
            _panel?.Close();
            _titleBar?.Dispose();
            _claudeWatch.Dispose();
            _signInWatch.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Shows the icon while Claude is open. It also stays put mid-switch (Claude is briefly closed) and while
    /// waiting for a new sign-in, so it never vanishes in the middle of something.
    /// </summary>
    private void UpdateVisibility()
    {
        bool visible;
        try
        {
            visible = !_hideWhileClaudeClosed || _busy || _prompting || _signInWatch.Enabled || _claude.IsRunning();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process list can fail when Windows is out of memory (e.g. "paging file is too small"). Keep the
            // icon as it is and try again on the next tick instead of crashing.
            return;
        }

        if (visible == _tray.Visible)
        {
            return;
        }

        _tray.Visible = visible;
        if (visible)
        {
            Greet();
        }
    }

    /// <summary>The first time the icon appears, point out anything the user should know.</summary>
    private void Greet()
    {
        if (_greeted)
        {
            return;
        }

        _greeted = true;
        var unsaved = SafeStatus() is { IsUnsaved: true };
        if (_startupJustEnabled)
        {
            Notify("Claude Roster now starts with Windows and appears whenever Claude is open. You can turn this off in its menu." +
                   (unsaved ? " Open the menu to save the account you're signed in to." : ""));
        }
        else if (unsaved)
        {
            Notify("Open the Claude Roster menu and save the account signed in to Claude, so you can switch back to it later.");
        }
    }

    private void OnShowRequested()
    {
        _hideWhileClaudeClosed = false;
        _claudeWatch.Stop();
        if (_tray.Visible)
        {
            Notify("Claude Roster is already running. Click this icon to switch accounts.");
            return;
        }

        UpdateVisibility();
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var status = SafeStatus();
        IReadOnlyList<AccountProfile> profiles;
        try
        {
            profiles = _switcher.ListProfiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _menu.Items.Add(new ToolStripMenuItem($"Couldn't read saved accounts: {exception.Message}") { Enabled = false });
            AddFooter();
            return;
        }

        _menu.Items.Add(new ToolStripMenuItem(Describe(status)) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var profile in profiles)
        {
            var active = status?.Profile?.Id == profile.Id;
            var ready = active || _switcher.HasSavedSignIn(profile);
            var item = new ToolStripMenuItem(ready ? Escape(profile.Alias) : $"{Escape(profile.Alias)} (sign-in not saved)")
            {
                Checked = active,
                Enabled = !_busy && ready,
            };
            item.Click += async (_, _) => await SwitchToAsync(profile);
            _menu.Items.Add(item);
        }

        if (profiles.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
        }

        if (status is { IsUnsaved: true })
        {
            AddItem(_menu.Items, "Save current account…", () => PromptSaveCurrent(SaveCurrentPrompt));
        }

        AddItem(_menu.Items, "Add another account…", async () => await AddAccountAsync());

        if (profiles.Count > 0)
        {
            var manage = new ToolStripMenuItem("Manage accounts") { Enabled = !_busy };
            var rename = new ToolStripMenuItem("Rename");
            var remove = new ToolStripMenuItem("Remove");
            foreach (var profile in profiles)
            {
                AddItem(rename.DropDownItems, Escape(profile.Alias), () => Rename(profile));
                if (status?.Profile?.Id != profile.Id)
                {
                    AddItem(remove.DropDownItems, Escape(profile.Alias), () => Remove(profile));
                }
            }

            remove.Enabled = remove.DropDownItems.Count > 0;
            manage.DropDownItems.AddRange([rename, remove]);
            _menu.Items.Add(manage);
        }

        AddFooter();
    }

    private void AddFooter()
    {
        _menu.Items.Add(new ToolStripSeparator());
        var share = new ToolStripMenuItem("Share Code sessions between accounts") { Checked = SafeShareSetting(), Enabled = !_busy };
        share.Click += (_, _) => ToggleSharing(!share.Checked);
        _menu.Items.Add(share);
        if (_titleBar is not null)
        {
            var titleBar = new ToolStripMenuItem("Show account button in Claude") { Checked = SafeTitleBarSetting() };
            titleBar.Click += (_, _) => ToggleTitleBar(!titleBar.Checked);
            _menu.Items.Add(titleBar);
        }

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupRegistration.IsEnabled() };
        startup.Click += (_, _) => StartupRegistration.SetEnabled(!startup.Checked);
        _menu.Items.Add(startup);
        AddItem(_menu.Items, "Open Claude Roster folder", OpenDataFolder);
        AddItem(_menu.Items, "Uninstall Claude Roster…", Uninstall);
        AddItem(_menu.Items, "Exit", ExitThread);
    }

    private void AddItem(ToolStripItemCollection items, string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Enabled = !_busy };
        item.Click += (_, _) => onClick();
        items.Add(item);
    }

    private async Task SwitchToAsync(AccountProfile profile)
    {
        if (_busy)
        {
            return;
        }

        var status = SafeStatus();
        if (status?.Profile?.Id == profile.Id)
        {
            return;
        }

        if (status is { IsUnsaved: true }
            && !PromptSaveCurrent("Before switching, name the account that's signed in now so you can come back to it."))
        {
            return;
        }

        if (!Dialogs.Confirm(SwitchConfirmation(profile.Alias, status)))
        {
            return;
        }

        await RunAsync($"Switching to {profile.Alias}…", async () =>
        {
            var outcome = await _switcher.SwitchAsync(profile.Id);
            Notify(outcome.Warning ?? $"Claude is now signed in as {profile.Alias}.");
        });
    }

    private async Task AddAccountAsync()
    {
        if (_busy)
        {
            return;
        }

        var status = SafeStatus();
        if (status is { IsUnsaved: true }
            && !PromptSaveCurrent("Before adding another account, name the one that's signed in now so you can come back to it."))
        {
            return;
        }

        if (!Dialogs.Confirm(AddAccountConfirmation(status)))
        {
            return;
        }

        await RunAsync("Getting Claude ready for a new sign-in…", async () =>
        {
            var outcome = await _switcher.PrepareNewSignInAsync();
            Notify(outcome.Warning ?? "Sign in to the other account in Claude.");
            _signInWatchEnds = DateTime.UtcNow + SignInWatchLimit;
            _signInWatch.Start();
        });
    }

    /// <summary>After "Add another account", waits for Claude to report who signed in, then asks for a name.</summary>
    private void CheckForNewSignIn()
    {
        if (_prompting || _busy)
        {
            return;
        }

        if (DateTime.UtcNow > _signInWatchEnds)
        {
            _signInWatch.Stop();
            return;
        }

        var status = SafeStatus();
        if (status?.AccountUuid is null)
        {
            return;
        }

        _signInWatch.Stop();
        if (status.Profile is not null)
        {
            Notify($"Claude is signed in as {status.Profile.Alias}.");
            return;
        }

        PromptSaveCurrent(NewAccountPrompt);
    }

    private bool PromptSaveCurrent(string prompt)
    {
        _prompting = true;
        try
        {
            var suggestion = $"Account {_switcher.ListProfiles().Count + 1}";
            var alias = AliasDialog.Ask(SaveCurrentTitle, prompt, suggestion);
            if (alias is null)
            {
                return false;
            }

            var profile = _switcher.SaveCurrent(alias);
            Notify($"Saved {profile.Alias}.");
            return true;
        }
        catch (ClaudeRosterException exception)
        {
            Dialogs.Error(exception.Message);
            return false;
        }
        finally
        {
            _prompting = false;
        }
    }

    private void ToggleSharing(bool enable)
    {
        if (enable && !Dialogs.Confirm(ShareConfirmation))
        {
            return;
        }

        try
        {
            _switcher.SetShareCodeSessions(enable);
            Notify(enable
                ? "Claude Code sessions will be shared from the next switch on."
                : "Sessions are no longer shared. Ones already copied stay in both accounts.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Dialogs.Error(exception.Message);
        }
    }

    internal const string ShareConfirmation =
        "Show every saved account's Claude Code sessions in all of them?\n\n" +
        "Each time you switch, sessions are copied between your accounts, so you can see and continue any session from either one. " +
        "Continuing a session sends that conversation through the account you're signed in to.\n\n" +
        "If one of these accounts belongs to your employer, check its policy before letting work sessions into a personal account.";

    private bool SafeShareSetting()
    {
        try
        {
            return _switcher.ShareCodeSessions;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private void ToggleTitleBar(bool enable)
    {
        try
        {
            _switcher.SetShowTitleBarButton(enable);
            _titleBar?.SetEnabled(enable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Dialogs.Error(exception.Message);
        }
    }

    private bool SafeTitleBarSetting()
    {
        try
        {
            return _switcher.ShowTitleBarButton;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// What the title bar button says: the account and its session usage left. Polled every second, so it skips the
    /// process scan.
    /// </summary>
    private TitleBarLabel TitleBarLabel()
    {
        LiveStatus? status;
        try
        {
            status = _switcher.PeekStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            status = null;
        }

        var now = DateTimeOffset.UtcNow;
        var sample = status?.Profile is { } signedIn && SafeUsage().TryGetValue(signedIn.Id, out var found) ? found : null;
        var left = sample?.SessionLeft(now);
        var stale = sample?.IsStale(now) == true;
        return status switch
        {
            null or { IsUnidentified: true } => new(AppName, null, "Switch Claude account (Claude Roster)"),
            // An old reading carries its age ("93% · 36m") so it can't pass for the live figure.
            { Profile: { } profile } => new(
                profile.ShortName ?? profile.Alias,
                left is null ? null : stale ? $"{left}% · {sample!.ShortAge(now)}" : $"{left}%",
                sample is { HasLimits: true }
                    ? $"{profile.Alias}: {sample.Summary(now)}\nClaude's reading from {sample.Taken(now)}" +
                      (stale ? ". Claude's usage page may show more." : "")
                    : $"Signed in as {profile.Alias}",
                stale,
                ColorOf(profile)),
            { AccountUuid: not null } => new("Account not saved", null, "Open to save the account signed in to Claude"),
            _ => new("Signed out", null, "Claude is signed out"),
        };
    }

    private IReadOnlyDictionary<string, UsageSample> SafeUsage()
    {
        try
        {
            return _switcher.GetUsage();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new Dictionary<string, UsageSample>();
        }
    }

    /// <summary>Opens the account panel; a click that just closed it (on the same button) leaves it closed.</summary>
    private void ShowPanel(Action<AccountPanel> open)
    {
        if (_panel is not null || DateTime.UtcNow - _panelClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        _panel = new AccountPanel(BuildPanel);
        _panel.FormClosed += (_, _) =>
        {
            _panel = null;
            _panelClosedAt = DateTime.UtcNow;
        };
        open(_panel);
    }

    /// <summary>The same choices as the tray menu, with each account's plan usage.</summary>
    internal IReadOnlyList<PanelItem> BuildPanel()
    {
        var items = new List<PanelItem>();
        var status = SafeStatus();
        IReadOnlyList<AccountProfile> profiles;
        try
        {
            profiles = _switcher.ListProfiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            items.Add(new PanelHeader($"Couldn't read saved accounts: {exception.Message}", IsNote: true));
            AddPanelFooter(items);
            return items;
        }

        items.Add(new PanelHeader(_busy ? "Switching…" : "Claude accounts"));
        switch (status)
        {
            case null or { IsUnidentified: true }:
                items.Add(new PanelHeader("Couldn't read Claude's sign-in", IsNote: true));
                break;
            case { IsUnsaved: true }:
                items.Add(new PanelHeader("Signed in to an account that isn't saved yet", IsNote: true));
                break;
            case { AccountUuid: null }:
                items.Add(new PanelHeader("Claude is signed out", IsNote: true));
                break;
        }

        var usage = SafeUsage();
        var now = DateTimeOffset.UtcNow;
        var anyStale = false;
        foreach (var profile in profiles)
        {
            var active = status?.Profile?.Id == profile.Id;
            var ready = active || _switcher.HasSavedSignIn(profile);
            usage.TryGetValue(profile.Id, out var sample);
            var stale = sample is { HasLimits: true } && sample.IsStale(now);
            anyStale |= stale;
            var more = new List<PanelItem>
            {
                new PanelAction(Glyphs.Rename, "Rename…", !_busy, () => Rename(profile)),
                new PanelAction(Glyphs.Tag, profile.ShortName is null ? "Short name for Claude's title bar…" : $"Short name: {profile.ShortName}…",
                    !_busy, () => SetShortName(profile)),
                new PanelAction(Glyphs.Color, "Colour", !_busy, () => { }, Submenu: ColorChoices(profile)),
                new PanelAction(Glyphs.Delete, active ? "Remove (switch away first)" : "Remove…", !_busy && !active, () => Remove(profile), Danger: true),
            };
            items.Add(new PanelAccount(
                profile.Alias,
                AccountDetail(active, ready, sample, now),
                sample?.SessionLeft(now),
                sample?.WeeklyLeft(now),
                active,
                Enabled: !_busy && ready && !active,
                async () => await SwitchToAsync(profile),
                more,
                stale,
                sample?.SessionResetsIn(now),
                sample?.WeeklyResetsIn(now),
                ColorOf(profile)));
        }

        if (anyStale)
        {
            // Claude only records a reading now and then; its own usage page has the live figures.
            items.Add(new PanelHeader("Grey figures are old readings from Claude", IsNote: true));
        }

        items.Add(new PanelSeparator());
        if (status is { IsUnsaved: true })
        {
            items.Add(new PanelAction(Glyphs.Save, "Save current account…", !_busy, () => PromptSaveCurrent(SaveCurrentPrompt)));
        }

        items.Add(new PanelAction(Glyphs.Add, "Add another account…", !_busy, async () => await AddAccountAsync()));
        AddPanelFooter(items);
        return items;
    }

    private void AddPanelFooter(List<PanelItem> items)
    {
        items.Add(new PanelSeparator());
        items.Add(new PanelToggle("Share Code sessions across accounts", SafeShareSetting(), !_busy, ToggleSharing));
        if (_titleBar is not null)
        {
            items.Add(new PanelToggle("Show account button in Claude", SafeTitleBarSetting(), true, ToggleTitleBar));
        }

        items.Add(new PanelToggle("Start with Windows", StartupRegistration.IsEnabled(), true, StartupRegistration.SetEnabled));
        items.Add(new PanelSeparator());
        items.Add(new PanelAction(Glyphs.Folder, "Open Claude Roster folder", true, OpenDataFolder));
        items.Add(new PanelAction(Glyphs.Delete, "Uninstall Claude Roster…", !_busy, Uninstall, Danger: true));
        items.Add(new PanelAction(Glyphs.Exit, "Exit", true, ExitThread));
    }

    private static string AccountDetail(bool active, bool ready, UsageSample? sample, DateTimeOffset now) => (active, sample) switch
    {
        (true, { HasLimits: true }) => $"Signed in · reading from {sample.Taken(now)}",
        (true, not null) => "Signed in · no usage limits reported",
        (true, null) => "Signed in",
        _ when !ready => "Sign-in not saved yet",
        (false, { HasLimits: true }) => $"Reading from {sample.Taken(now)}",
        (false, not null) => $"No usage limits reported · {sample.Age(now)}",
        _ => "No usage recorded yet",
    };

    /// <summary>Segoe Fluent Icons / MDL2 code points.</summary>
    private static class Glyphs
    {
        public const string Add = "\uE710";
        public const string Save = "\uE74E";
        public const string Rename = "\uE8AC";
        public const string Tag = "\uE8EC";
        public const string Color = "\uE790";
        public const string Delete = "\uE74D";
        public const string Folder = "\uE8B7";
        public const string Exit = "\uE7E8";
    }

    internal static string UninstallConfirmation(string? signedIn) =>
        "Uninstall Claude Roster?\n\n" +
        "This deletes every saved account and backup on this PC, turns off Start with Windows and removes Claude Roster itself. It can't be undone.\n\n" +
        $"Claude stays signed in to {(signedIn is null ? "its current account" : signedIn)}. To use your other accounts again, sign in to them in Claude. " +
        "Claude's own data and your Claude Code sessions aren't touched.";

    /// <summary>
    /// Removes everything: the saved accounts first (if that fails, nothing else is touched, so it can be retried),
    /// then the registry entries, then the exe once this process has exited.
    /// </summary>
    private void Uninstall()
    {
        if (_busy || !Dialogs.Confirm(UninstallConfirmation(SafeStatus()?.Profile?.Alias)))
        {
            return;
        }

        try
        {
            _switcher.DeleteAllData();
        }
        catch (Exception exception) when (exception is ClaudeRosterException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"Couldn't delete the saved accounts, so nothing was uninstalled: {exception.Message}");
            return;
        }

        try
        {
            StartupRegistration.Forget();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Dialogs.Error($"The saved accounts are gone, but Claude Roster's registry settings couldn't all be removed: {exception.Message}");
        }

        ScheduleSelfDelete();
        ExitThread();
    }

    /// <summary>
    /// A running exe can't delete itself, so a hidden cmd waits a few seconds for this one to exit, then deletes it,
    /// the command-line tool if it sits alongside, and the folder if that leaves it empty.
    /// </summary>
    private static void ScheduleSelfDelete()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(exe)!;
        var cli = Path.Combine(folder, "claude-roster.exe");
        var command = $"/d /c ping -n 4 127.0.0.1 >nul & del /f /q \"{exe}\" & del /f /q \"{cli}\" & rmdir \"{folder}\"";
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
    }

    /// <summary>The account's colour: the one chosen, or the one its name picks.</summary>
    internal static Color ColorOf(AccountProfile profile) =>
        AccountSwitcher.IsColor(profile.Color) ? ColorTranslator.FromHtml(profile.Color!) : AccountPanel.DefaultColor(profile.Alias);

    private IReadOnlyList<PanelItem> ColorChoices(AccountProfile profile)
    {
        var items = new List<PanelItem>
        {
            new PanelAction(Glyphs.Color, "Automatic", true, () => SetColor(profile, null),
                Swatch: AccountPanel.DefaultColor(profile.Alias), Selected: profile.Color is null),
        };
        foreach (var (name, color) in AccountPanel.Swatches)
        {
            var hex = ColorTranslator.ToHtml(color).ToUpperInvariant();
            items.Add(new PanelAction(Glyphs.Color, name, true, () => SetColor(profile, hex), Swatch: color,
                Selected: string.Equals(profile.Color, hex, StringComparison.OrdinalIgnoreCase)));
        }

        return items;
    }

    private void SetColor(AccountProfile profile, string? color)
    {
        try
        {
            _switcher.SetColor(profile.Id, color);
        }
        catch (Exception exception) when (exception is ClaudeRosterException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(exception.Message);
        }
    }

    private void SetShortName(AccountProfile profile)
    {
        var shortName = AliasDialog.AskOptional(
            "Short name",
            $"A short name for {profile.Alias} on the button in Claude's title bar, up to {AccountSwitcher.MaxShortNameLength} characters. " +
            "Leave it empty to use the full name.",
            profile.ShortName ?? "",
            AccountSwitcher.MaxShortNameLength);
        if (shortName is null)
        {
            return;
        }

        try
        {
            _switcher.SetShortName(profile.Id, shortName);
        }
        catch (Exception exception) when (exception is ClaudeRosterException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(exception.Message);
        }
    }

    private void Rename(AccountProfile profile)
    {
        var alias = AliasDialog.Ask("Rename account", $"New name for {profile.Alias}:", profile.Alias);
        if (alias is null)
        {
            return;
        }

        try
        {
            _switcher.Rename(profile.Id, alias);
        }
        catch (ClaudeRosterException exception)
        {
            Dialogs.Error(exception.Message);
        }
    }

    private void Remove(AccountProfile profile)
    {
        if (!Dialogs.Confirm($"Remove {profile.Alias} from Claude Roster?\n\nIts saved sign-in on this PC is deleted. The Claude account itself isn't affected."))
        {
            return;
        }

        try
        {
            _switcher.Remove(profile.Id);
        }
        catch (Exception exception) when (exception is ClaudeRosterException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(exception.Message);
        }
    }

    private async Task RunAsync(string busyText, Func<Task> operation)
    {
        _busy = true;
        _tray.Text = busyText.Length > 120 ? busyText[..120] : busyText;
        try
        {
            await operation();
        }
        catch (ClaudeRosterException exception)
        {
            Dialogs.Error(exception.Message);
        }
        catch (Exception exception)
        {
            // Last line of defence for an event handler: report instead of crashing the tray app.
            Dialogs.Error($"Something went wrong: {exception.Message}");
        }
        finally
        {
            _busy = false;
            _tray.Text = AppName;
        }
    }

    private LiveStatus? SafeStatus()
    {
        try
        {
            return _switcher.GetStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_switcher.Paths.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_switcher.Paths.Root}\"") { UseShellExecute = false })?.Dispose();
    }

    private void Notify(string text) => _tray.ShowBalloonTip(5000, AppName, text, ToolTipIcon.Info);

    private static string Describe(LiveStatus? status) => status switch
    {
        null or { IsUnidentified: true } => "Couldn't read Claude's sign-in",
        { Profile: { } profile } => $"Signed in: {Escape(profile.Alias)}",
        { AccountUuid: not null } => "Signed in: account not saved yet",
        _ => "Claude is signed out",
    };

    private static string SessionWarning(LiveStatus? status)
    {
        var sessions = status?.Process.CodeSessionCount ?? 0;
        return sessions switch
        {
            0 => "",
            1 => "\n\n1 running Claude Code session will stop.",
            _ => $"\n\n{sessions} running Claude Code sessions will stop.",
        };
    }

    /// <summary>Menus treat &amp; as a mnemonic marker.</summary>
    private static string Escape(string text) => text.Replace("&", "&&");
}
