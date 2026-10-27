using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

/// <param name="AccountUuid">The account Claude is signed in to, or null when it's signed out.</param>
/// <param name="Profile">The saved profile for that account, if there is one.</param>
/// <param name="IsUnidentified">Claude's sign-in couldn't be read, so it isn't safe to switch.</param>
public sealed record LiveStatus(string? AccountUuid, AccountProfile? Profile, bool IsUnidentified, ClaudeRunState Process)
{
    public bool IsUnsaved => AccountUuid is not null && Profile is null;
}

/// <param name="Warning">Set when the switch worked but something after it (reopening Claude) didn't.</param>
public sealed record SwitchOutcome(AccountProfile? Profile, string? Warning);

/// <summary>
/// Moves Claude between saved accounts: close Claude, back up the live sign-in, mirror it into the profile it
/// belongs to, put the target's sign-in in place, reopen. A failure while changing files restores the backup.
/// </summary>
public sealed class AccountSwitcher
{
    public const int MaxAliasLength = 40;
    public const int MaxShortNameLength = 14;
    private const int BackupsToKeep = 5;

    private const string UnidentifiedMessage =
        "Couldn't tell which account Claude is signed in to, so switching is paused to keep that sign-in safe. " +
        "Try again in a moment; if it keeps happening, Claude may have changed how it stores sign-ins.";

    private readonly ClaudeInstall _install;
    private readonly RosterPaths _paths;
    private readonly IProfileStore _store;
    private readonly IClaudeProcess _process;
    private readonly ISessionSnapshotter _snapshots;
    private readonly TimeProvider _time;
    private readonly string? _addOnStore;

    public AccountSwitcher(
        ClaudeInstall install,
        RosterPaths paths,
        IProfileStore? store = null,
        IClaudeProcess? process = null,
        ISessionSnapshotter? snapshots = null,
        TimeProvider? time = null,
        string? addOnStore = null)
    {
        _install = install;
        // A sandboxed Claude (tests, dry runs, the screenshot tool) leaves the real add-on's store out too.
        _addOnStore = addOnStore ?? (install.PackageFamilyName is null && install.LauncherPath is null ? null : PlanUsage.DefaultAddOnStoreFolder());
        _paths = paths;
        _time = time ?? TimeProvider.System;
        _store = store ?? new JsonProfileStore(paths);
        _process = process ?? new ClaudeProcess(install);
        _snapshots = snapshots ?? new SessionSnapshotter(_time);
    }

    public ClaudeInstall Install => _install;
    public RosterPaths Paths => _paths;

    public IReadOnlyList<AccountProfile> ListProfiles() =>
        _store.Load().Profiles.OrderBy(profile => profile.Alias, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The plan usage Claude last recorded for each saved account, keyed by profile id.</summary>
    public IReadOnlyDictionary<string, UsageSample> GetUsage() => PlanUsage.ForProfiles(_install.DataRoot, ListProfiles(), _addOnStore);

    public bool HasSavedSignIn(AccountProfile profile) => _snapshots.ReadInfo(_paths.SessionDirectory(profile.Id)) is not null;

    public bool ShareCodeSessions => _store.Load().ShareCodeSessions;

    /// <summary>Takes effect at the next switch, the next time Claude is closed.</summary>
    public void SetShareCodeSessions(bool enabled)
    {
        var state = _store.Load();
        state.ShareCodeSessions = enabled;
        _store.Save(state);
    }

    public bool ShowTitleBarButton => _store.Load().ShowTitleBarButton;

    public void SetShowTitleBarButton(bool enabled)
    {
        var state = _store.Load();
        state.ShowTitleBarButton = enabled;
        _store.Save(state);
    }

    public LiveStatus GetStatus() => GetStatus(_store.Load());

    /// <summary>Who is signed in, without looking at Claude's processes; cheap enough to poll.</summary>
    public LiveStatus PeekStatus() => GetStatus(_store.Load(), ClaudeRunState.NotRunning);

    /// <summary>
    /// Adds the account Claude is signed in to now. Its sign-in is copied the next time you switch away from it,
    /// because Claude holds those files open while it runs.
    /// </summary>
    public AccountProfile SaveCurrent(string alias)
    {
        var state = _store.Load();
        var status = GetStatus(state);
        if (status.AccountUuid is null)
        {
            throw new ClaudeRosterException("Claude isn't signed in, so there's no account to save yet.");
        }

        if (status.Profile is not null)
        {
            return status.Profile;
        }

        var now = _time.GetUtcNow();
        var profile = new AccountProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Alias = CheckAlias(state, alias, ignoreProfileId: null),
            AccountUuid = status.AccountUuid,
            CreatedAt = now,
            LastUsedAt = now,
        };
        state.Profiles.Add(profile);
        _store.Save(state);
        return profile;
    }

    public void Rename(string profileId, string alias)
    {
        var state = _store.Load();
        var profile = Require(state, profileId);
        profile.Alias = CheckAlias(state, alias, profile.Id);
        _store.Save(state);
    }

    /// <summary>Sets the account's colour (#RRGGBB), or with null goes back to the one picked from its name.</summary>
    public void SetColor(string profileId, string? color)
    {
        if (color is not null && !IsColor(color))
        {
            throw new ClaudeRosterException($"{color} isn't a colour like #3E7CB1.");
        }

        var state = _store.Load();
        Require(state, profileId).Color = color?.ToUpperInvariant();
        _store.Save(state);
    }

    /// <summary>Sets the name for Claude's title bar button; blank goes back to the account's name.</summary>
    public void SetShortName(string profileId, string? shortName)
    {
        var trimmed = shortName?.Trim();
        if (trimmed?.Length > MaxShortNameLength)
        {
            throw new ClaudeRosterException($"Keep the short name to {MaxShortNameLength} characters or fewer.");
        }

        var state = _store.Load();
        Require(state, profileId).ShortName = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        _store.Save(state);
    }

    public static bool IsColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>Forgets a saved account and deletes its saved sign-in from this PC. The account itself is untouched.</summary>
    public void Remove(string profileId)
    {
        var state = _store.Load();
        var profile = Require(state, profileId);
        if (GetStatus(state).Profile?.Id == profile.Id)
        {
            throw new ClaudeRosterException($"{profile.Alias} is signed in right now. Switch to another account before removing it.");
        }

        state.Profiles.Remove(profile);
        _store.Save(state);
        FileOps.DeleteDirectory(_paths.ProfileDirectory(profile.Id));
    }

    /// <summary>
    /// For uninstalling: deletes everything the roster saved (every account's sign-in, the backups, its settings).
    /// Claude's own data is left alone, so Claude stays signed in to its current account.
    /// </summary>
    public void DeleteAllData()
    {
        var root = Path.GetFullPath(_paths.Root);
        if (!Directory.Exists(root))
        {
            return;
        }

        // CLAUDE_ROSTER_HOME can point anywhere; only delete a folder that is plainly the roster's own.
        var looksLikeRoster = File.Exists(_paths.StateFile)
                              || string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "ClaudeRoster", StringComparison.OrdinalIgnoreCase);
        if (Path.GetPathRoot(root) == root || !looksLikeRoster || IsSameOrInside(_install.DataRoot, root))
        {
            throw new ClaudeRosterException($"{root} doesn't look like Claude Roster's own data folder, so it was left alone.");
        }

        FileOps.DeleteDirectory(root);
    }

    public async Task<SwitchOutcome> SwitchAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var state = _store.Load();
        var target = Require(state, profileId);
        var before = Preflight(state);
        if (before.Profile?.Id == target.Id)
        {
            var warning = before.Process.IsRunning ? null : await TryOpenAsync(before.Process, cancellationToken);
            return new SwitchOutcome(target, warning);
        }

        var targetSession = _paths.SessionDirectory(target.Id);
        if (_snapshots.ReadInfo(targetSession) is null)
        {
            throw new ClaudeRosterException($"There's no saved sign-in for {target.Alias} yet.");
        }

        return await ReplaceSignInAsync(
            state, before.Process, () => _snapshots.Apply(targetSession, _install.DataRoot), target, cancellationToken);
    }

    /// <summary>Saves the current account, then leaves Claude on its sign-in screen for another one.</summary>
    public Task<SwitchOutcome> PrepareNewSignInAsync(CancellationToken cancellationToken = default)
    {
        var state = _store.Load();
        var before = Preflight(state);
        return ReplaceSignInAsync(
            state, before.Process, () => _snapshots.ClearSignIn(_install.DataRoot), target: null, cancellationToken);
    }

    private LiveStatus GetStatus(RosterState state) => GetStatus(state, _process.Capture());

    private LiveStatus GetStatus(RosterState state, ClaudeRunState process)
    {
        JsonObject? config;
        try
        {
            config = ClaudeConfig.Load(_install.DataRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            config = null;
        }

        var accountUuid = config is null ? null : ClaudeConfig.ReadAccountUuid(config);
        // Tokens without an account id means the format changed under us; refuse rather than guess who owns them.
        var unidentified = config is null || (accountUuid is null && ClaudeConfig.HasTokens(config));
        return new LiveStatus(accountUuid, state.FindByAccount(accountUuid), unidentified, process);
    }

    private LiveStatus Preflight(RosterState state)
    {
        var status = GetStatus(state);
        if (status.IsUnidentified)
        {
            throw new ClaudeRosterException(UnidentifiedMessage);
        }

        if (status.IsUnsaved)
        {
            throw new UnsavedAccountException();
        }

        return status;
    }

    private async Task<SwitchOutcome> ReplaceSignInAsync(
        RosterState state,
        ClaudeRunState before,
        Action activate,
        AccountProfile? target,
        CancellationToken cancellationToken)
    {
        SecureDirectory.Ensure(_paths.Root);
        await _process.CloseAsync(cancellationToken);

        string backup;
        try
        {
            if (_process.IsDataRootInUse())
            {
                throw new ClaudeRosterException("Claude's data folder is still in use. Quit Claude from its tray icon and try again.");
            }

            // Claude may have refreshed its tokens, or been signed in elsewhere, while it closed: look again.
            var closed = Preflight(state);
            backup = _paths.NewBackupDirectory(_time.GetUtcNow());
            _snapshots.Capture(_install.DataRoot, backup);
            if (closed.Profile is not null)
            {
                _snapshots.Capture(_install.DataRoot, _paths.SessionDirectory(closed.Profile.Id));
            }
        }
        catch
        {
            // Nothing in Claude's folder has changed yet; just put Claude back the way it was.
            if (before.IsRunning)
            {
                await TryOpenAsync(before, CancellationToken.None);
            }

            throw;
        }

        try
        {
            activate();
        }
        catch (Exception exception)
        {
            try
            {
                _snapshots.Apply(backup, _install.DataRoot);
            }
            catch (Exception restoreException)
            {
                throw new ClaudeRosterException(
                    $"Switching failed and the previous sign-in couldn't be put back automatically. A full copy is in {backup}.",
                    new AggregateException(exception, restoreException));
            }

            if (before.IsRunning)
            {
                await TryOpenAsync(before, CancellationToken.None);
            }

            throw new ClaudeRosterException("Switching failed, so your previous account was put back.", exception);
        }

        var sharingWarning = state.ShareCodeSessions ? TryShareSessions(state, backup) : null;
        if (target is not null)
        {
            target.LastUsedAt = _time.GetUtcNow();
            _store.Save(state);
        }

        PruneBackups();
        var openWarning = await TryOpenAsync(before, cancellationToken);
        return new SwitchOutcome(target, openWarning is null ? sharingWarning : sharingWarning is null ? openWarning : $"{sharingWarning} {openWarning}");
    }

    /// <summary>Sharing rides on a switch that has already worked, so a failure here only warns.</summary>
    private string? TryShareSessions(RosterState state, string backup)
    {
        var sharing = new SessionSharing(_install.DataRoot, _paths.SharedSessionsStateFile);
        var accounts = state.Profiles.Select(profile => profile.AccountUuid).ToList();
        try
        {
            foreach (var folder in sharing.AccountFolders(accounts))
            {
                FileOps.CopyDirectory(folder, Path.Combine(backup, SessionSharing.SessionsFolderName, Path.GetFileName(folder)));
            }

            sharing.Sync(accounts);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Switched, but Claude Code sessions couldn't be shared this time: {exception.Message}";
        }
    }

    private async Task<string?> TryOpenAsync(ClaudeRunState before, CancellationToken cancellationToken)
    {
        try
        {
            await _process.OpenAsync(before, cancellationToken);
            return null;
        }
        catch (ClaudeRosterException exception)
        {
            return exception.Message;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return "Claude couldn't be reopened automatically. Open it from the Start menu.";
        }
    }

    private void PruneBackups()
    {
        if (!Directory.Exists(_paths.BackupsDirectory))
        {
            return;
        }

        // Names start with a UTC timestamp, so ordinal order is age order.
        foreach (var old in Directory.GetDirectories(_paths.BackupsDirectory)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(BackupsToKeep))
        {
            try
            {
                FileOps.DeleteDirectory(old);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover backup is harmless; the next switch tries again.
            }
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, Path.GetFullPath(path));
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static AccountProfile Require(RosterState state, string profileId) =>
        state.Find(profileId) ?? throw new ClaudeRosterException("That account isn't in the roster anymore.");

    private static string CheckAlias(RosterState state, string alias, string? ignoreProfileId)
    {
        var trimmed = alias.Trim();
        if (trimmed.Length == 0)
        {
            throw new ClaudeRosterException("Give the account a name.");
        }

        if (trimmed.Length > MaxAliasLength)
        {
            throw new ClaudeRosterException($"Keep the name to {MaxAliasLength} characters or fewer.");
        }

        if (state.Profiles.Any(profile =>
                profile.Id != ignoreProfileId
                && string.Equals(profile.Alias, trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ClaudeRosterException($"There's already an account named {trimmed}.");
        }

        return trimmed;
    }
}
