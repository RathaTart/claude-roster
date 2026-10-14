using System.Diagnostics;
using System.Reflection;
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
    private readonly Icon _icon = TrayIcon.Create();
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _signInWatch = new() { Interval = 2000 };
    private DateTime _signInWatchEnds;
    private bool _busy;
    private bool _prompting;

    public TrayContext(AccountSwitcher switcher)
    {
        _switcher = switcher;
        _menu.Opening += (_, _) => RebuildMenu();
        _tray = new NotifyIcon { Icon = _icon, Text = AppName, ContextMenuStrip = _menu, Visible = true };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMenu();
            }
        };
        _signInWatch.Tick += (_, _) => CheckForNewSignIn();

        if (SafeStatus() is { IsUnsaved: true })
        {
            Notify("Open the Claude Roster menu and save the account signed in to Claude, so you can switch back to it later.");
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
            _signInWatch.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
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
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupRegistration.IsEnabled() };
        startup.Click += (_, _) => StartupRegistration.SetEnabled(!startup.Checked);
        _menu.Items.Add(startup);
        AddItem(_menu.Items, "Open Claude Roster folder", OpenDataFolder);
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

    private void ShowMenu() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);

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
