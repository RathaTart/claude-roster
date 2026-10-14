using ClaudeRoster.Core;

namespace ClaudeRoster;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var background = args.Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        // A second launch hands over to the running copy (bringing its icon out) instead of starting another.
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeRoster.Show");
        using var instance = new Mutex(initiallyOwned: true, @"Local\ClaudeRoster.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!background)
            {
                showRequest.Set();
            }

            return;
        }

        ApplicationConfiguration.Initialize();

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
            if (!background)
            {
                Dialogs.Error("Couldn't find the Claude desktop app's data folder. Install Claude, sign in once, then start Claude Roster again.");
            }

            return;
        }

        var startupJustEnabled = StartupRegistration.ApplyDefault();
        Application.Run(new TrayContext(
            new AccountSwitcher(install, RosterPaths.Default()),
            hideWhileClaudeClosed: background,
            startupJustEnabled: startupJustEnabled,
            showRequest: showRequest));
    }
}
