using ClaudeRoster.Core;

var install = ClaudeInstall.Locate();
if (install is null)
{
    Console.Error.WriteLine("Couldn't find the Claude desktop app's data folder.");
    return 1;
}

var switcher = new AccountSwitcher(install, RosterPaths.Default());
var assumeYes = args.Any(arg => arg is "--yes" or "-y");
var operands = args.Skip(1).Where(arg => arg is not ("--yes" or "-y")).ToArray();

try
{
    switch (args.FirstOrDefault()?.ToLowerInvariant())
    {
        case "status":
            PrintStatus();
            return 0;
        case "list":
            PrintList();
            return 0;
        case "plan":
            PrintPlan();
            return 0;
        case "save" when operands.Length == 1:
            Console.WriteLine($"Saved {switcher.SaveCurrent(operands[0]).Alias}.");
            return 0;
        case "switch" when operands.Length == 1:
        {
            RequireOutsideClaude();
            var profile = FindProfile(operands[0]);
            if (!Confirm($"Switch Claude to {profile.Alias}? Claude will close and reopen."))
            {
                return 1;
            }

            var outcome = await switcher.SwitchAsync(profile.Id);
            Console.WriteLine(outcome.Warning ?? $"Claude is now signed in as {profile.Alias}.");
            return 0;
        }
        case "add":
        {
            RequireOutsideClaude();
            if (!Confirm("Claude will close and reopen on its sign-in screen. Continue?"))
            {
                return 1;
            }

            var outcome = await switcher.PrepareNewSignInAsync();
            Console.WriteLine(outcome.Warning ?? "Sign in to the other account in Claude, then run: claude-roster save <name>");
            return 0;
        }
        case "share" when operands is ["on" or "off"]:
        {
            var enable = operands[0] == "on";
            if (enable && !Confirm(
                    "Copy Claude Code sessions between all saved accounts on each switch? Continuing a session sends it through " +
                    "the account you're signed in to; check your employer's policy before sharing work sessions."))
            {
                return 1;
            }

            switcher.SetShareCodeSessions(enable);
            Console.WriteLine(enable ? "Sessions will be shared from the next switch on." : "Sessions are no longer shared.");
            return 0;
        }
        case "rename" when operands.Length == 2:
            switcher.Rename(FindProfile(operands[0]).Id, operands[1]);
            return 0;
        case "remove" when operands.Length == 1:
        {
            var profile = FindProfile(operands[0]);
            if (!Confirm($"Remove {profile.Alias} and delete its saved sign-in from this PC?"))
            {
                return 1;
            }

            switcher.Remove(profile.Id);
            return 0;
        }
        case null or "help" or "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            PrintUsage();
            return 2;
    }
}
catch (ClaudeRosterException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

void PrintStatus()
{
    var status = switcher.GetStatus();
    var signedIn = status switch
    {
        { IsUnidentified: true } => "couldn't read Claude's sign-in",
        { Profile: { } profile } => $"{profile.Alias} ({Mask(status.AccountUuid)})",
        { AccountUuid: not null } => $"account not saved yet ({Mask(status.AccountUuid)})",
        _ => "signed out",
    };
    var running = status.Process.IsRunning
        ? $"yes, {status.Process.CodeSessionCount} Claude Code session(s) open"
        : "no";

    Console.WriteLine($"Claude data     {install.DataRoot}");
    Console.WriteLine($"Store package   {install.PackageFamilyName ?? "(classic install)"}");
    Console.WriteLine($"Claude running  {running}");
    Console.WriteLine($"Signed in       {signedIn}");
    Console.WriteLine($"Saved accounts  {switcher.ListProfiles().Count}");
    Console.WriteLine($"Share sessions  {(switcher.ShareCodeSessions ? "on" : "off")}");
    Console.WriteLine($"Roster data     {switcher.Paths.Root}");
    if (LaunchContext.IsStartedFromClaude())
    {
        Console.WriteLine("Note            started from inside Claude, so switch and add are disabled here");
    }
}

void PrintList()
{
    var active = switcher.GetStatus().Profile?.Id;
    foreach (var profile in switcher.ListProfiles())
    {
        var marker = profile.Id == active ? "*" : " ";
        var saved = profile.Id == active || switcher.HasSavedSignIn(profile) ? "" : "  (sign-in not saved yet)";
        Console.WriteLine($"{marker} {profile.Alias,-24} {Mask(profile.AccountUuid)}{saved}");
    }
}

// A dry run: what a switch would copy, without touching anything.
void PrintPlan()
{
    Console.WriteLine($"Saved and swapped on each switch, from {install.DataRoot}:");
    foreach (var entry in SessionSnapshotter.Describe(install.DataRoot))
    {
        Console.WriteLine($"  {entry.RelativePath,-24} {(entry.Exists ? $"{entry.Bytes / 1024.0:N0} KB" : "(not present)")}");
    }

    var keys = ClaudeConfig.AccountKeyNames(ClaudeConfig.Load(install.DataRoot));
    Console.WriteLine($"  config.json keys         {(keys.Count == 0 ? "(none)" : string.Join(", ", keys))}");
    Console.WriteLine("Left alone: everything else (settings, Claude Code sessions, extensions, caches).");
}

AccountProfile FindProfile(string nameOrId) =>
    switcher.ListProfiles().FirstOrDefault(profile =>
        string.Equals(profile.Alias, nameOrId, StringComparison.CurrentCultureIgnoreCase) || profile.Id == nameOrId)
    ?? throw new ClaudeRosterException($"No saved account named {nameOrId}. Run: claude-roster list");

void RequireOutsideClaude()
{
    if (LaunchContext.IsStartedFromClaude())
    {
        throw new ClaudeRosterException(
            "This terminal was started from inside Claude, and switching closes Claude. Run it from a normal terminal instead.");
    }
}

bool Confirm(string question)
{
    if (assumeYes)
    {
        return true;
    }

    Console.Write($"{question} [y/N] ");
    return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
}

static string Mask(string? accountUuid) => accountUuid is null ? "-" : accountUuid[..8] + "…";

static void PrintUsage() => Console.WriteLine(
    """
    claude-roster: switch the Claude desktop app between saved accounts.

      status                 what Claude is signed in to right now
      list                   saved accounts (* = signed in)
      plan                   dry run: what a switch copies
      save <name>            save the account Claude is signed in to
      switch <name>          close Claude, swap accounts, reopen
      add                    save the current account, then open Claude's sign-in screen
      share on|off           copy Claude Code sessions between accounts on each switch
      rename <name> <new>    rename a saved account
      remove <name>          forget a saved account (not the signed-in one)

      -y, --yes              don't ask before closing Claude or deleting
    """);
