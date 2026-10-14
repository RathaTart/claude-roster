using ClaudeRoster.Core;

namespace ClaudeRoster;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\ClaudeRoster.Tray", out var isFirstInstance);
        ApplicationConfiguration.Initialize();

        if (!isFirstInstance)
        {
            Dialogs.Info("Claude Roster is already running. Look for its icon in the system tray (it may be under the ^ arrow).");
            return;
        }

        if (LaunchContext.IsStartedFromClaude())
        {
            Dialogs.Error(
                "Claude Roster was started from inside the Claude app. Switching accounts closes Claude, which would stop Claude Roster too.\n\n" +
                "Start ClaudeRoster.exe from File Explorer or the Start menu instead.");
            return;
        }

        var install = ClaudeInstall.Locate();
        if (install is null)
        {
            Dialogs.Error("Couldn't find the Claude desktop app's data folder. Install Claude, sign in once, then start Claude Roster again.");
            return;
        }

        Application.Run(new TrayContext(new AccountSwitcher(install, RosterPaths.Default())));
    }
}
