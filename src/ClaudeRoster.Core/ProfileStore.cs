using System.Text.Json;

namespace ClaudeRoster.Core;

public sealed class AccountProfile
{
    public required string Id { get; init; }
    public required string Alias { get; set; }

    /// <summary>Claude's id for the account (lastKnownAccountUuid); how the roster recognises who is signed in.</summary>
    public required string AccountUuid { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>The colour the account wears in the panel and on Claude's title bar, as #RRGGBB; null picks one.</summary>
    public string? Color { get; set; }

    /// <summary>What the button on Claude's title bar calls it, when the full name is too long; null uses the name.</summary>
    public string? ShortName { get; set; }
}

public sealed class RosterState
{
    public int Version { get; set; } = 1;
    public List<AccountProfile> Profiles { get; set; } = [];

    /// <summary>Copy Claude Code sessions between saved accounts on each switch, so every account lists them all.</summary>
    public bool ShareCodeSessions { get; set; }

    /// <summary>Show the account button pinned to the top left of Claude's window.</summary>
    public bool ShowTitleBarButton { get; set; } = true;

    public AccountProfile? Find(string id) => Profiles.FirstOrDefault(profile => profile.Id == id);

    public AccountProfile? FindByAccount(string? accountUuid) =>
        accountUuid is null
            ? null
            : Profiles.FirstOrDefault(profile => string.Equals(profile.AccountUuid, accountUuid, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Layout of the roster's own data. Everything sensitive lives under one ACL-restricted root.</summary>
public sealed class RosterPaths(string root)
{
    public const string HomeOverrideVariable = "CLAUDE_ROSTER_HOME";

    public string Root { get; } = root;
    public string StateFile => Path.Combine(Root, "profiles.json");
    public string ProfilesDirectory => Path.Combine(Root, "profiles");
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string SharedSessionsStateFile => Path.Combine(Root, "shared-sessions.json");

    public string ProfileDirectory(string profileId) =>
        IsValidProfileId(profileId)
            ? Path.Combine(ProfilesDirectory, profileId)
            : throw new ArgumentException("Not a roster profile id.", nameof(profileId));

    public string SessionDirectory(string profileId) => Path.Combine(ProfileDirectory(profileId), "session");

    public string NewBackupDirectory(DateTimeOffset now) =>
        Path.Combine(BackupsDirectory, $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

    /// <summary>Ids become folder names, so only the shape the roster generates is accepted.</summary>
    public static bool IsValidProfileId(string? id) => Guid.TryParseExact(id, "N", out _);

    public static RosterPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeOverrideVariable);
        return new RosterPaths(!string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRoster"));
    }
}

public interface IProfileStore
{
    RosterState Load();
    void Save(RosterState state);
}

public sealed class JsonProfileStore(RosterPaths paths) : IProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RosterState Load()
    {
        if (!File.Exists(paths.StateFile))
        {
            return new RosterState();
        }

        var state = JsonSerializer.Deserialize<RosterState>(File.ReadAllText(paths.StateFile), Options) ?? new RosterState();
        if (state.Profiles.Any(profile => !RosterPaths.IsValidProfileId(profile.Id)))
        {
            throw new InvalidDataException($"{paths.StateFile} contains a profile id the roster didn't create.");
        }

        return state;
    }

    public void Save(RosterState state)
    {
        SecureDirectory.Ensure(paths.Root);
        AtomicFile.WriteAllText(paths.StateFile, JsonSerializer.Serialize(state, Options));
    }
}
