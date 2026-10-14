using System.Text.Json;

namespace ClaudeRoster.Core;

/// <param name="Copied">Session files copied into another account's list.</param>
/// <param name="Deleted">Sessions deleted in one account and therefore removed from the others.</param>
public sealed record SharingResult(int Copied, int Deleted);

/// <summary>
/// Gives every saved account the same Claude Code session list. Claude keeps one folder of session files per
/// account and organisation (claude-code-sessions\&lt;account&gt;\&lt;org&gt;\local_*.json); the conversations
/// themselves live in the shared ~\.claude folder, so a copied session file is all another account needs to list
/// and continue that session. Only runs while Claude is closed.
/// </summary>
public sealed class SessionSharing(string dataRoot, string stateFile)
{
    public const string SessionsFolderName = "claude-code-sessions";
    private const string SessionFilePattern = "local_*.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SessionsRoot => Path.Combine(dataRoot, SessionsFolderName);

    /// <summary>The account folders that exist; the backup step copies these before syncing.</summary>
    public IReadOnlyList<string> AccountFolders(IEnumerable<string> accountUuids) =>
        accountUuids
            .Where(uuid => Guid.TryParse(uuid, out _))
            .Select(uuid => Path.Combine(SessionsRoot, uuid))
            .Where(Directory.Exists)
            .ToList();

    public SharingResult Sync(IEnumerable<string> accountUuids)
    {
        // An account that has never opened the Code tab has no org folder yet; it joins once it does.
        var folders = AccountFolders(accountUuids)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(folder => Guid.TryParse(Path.GetFileName(folder), out _))
            .ToList();
        if (folders.Count < 2)
        {
            return new SharingResult(0, 0);
        }

        // A session that was in a folder at the last sync and is gone now was deleted there, so delete it everywhere.
        // Folders new to sharing have no history, so they never cause deletions.
        var previous = LoadState();
        var deleted = folders
            .SelectMany(folder => previous.TryGetValue(Key(folder), out var names)
                ? names.Where(name => !File.Exists(Path.Combine(folder, name)))
                : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            foreach (var name in deleted)
            {
                FileOps.DeleteFile(Path.Combine(folder, name));
            }
        }

        // Everything else: every folder gets the most recently written copy of every session.
        var newest = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in folders.SelectMany(folder => new DirectoryInfo(folder).EnumerateFiles(SessionFilePattern)))
        {
            if (!newest.TryGetValue(file.Name, out var current) || file.LastWriteTimeUtc > current.LastWriteTimeUtc)
            {
                newest[file.Name] = file;
            }
        }

        var copied = 0;
        foreach (var folder in folders)
        {
            foreach (var source in newest.Values)
            {
                var target = new FileInfo(Path.Combine(folder, source.Name));
                if (target.Exists && target.LastWriteTimeUtc >= source.LastWriteTimeUtc)
                {
                    continue;
                }

                // File.Copy keeps the write time, so the copies compare equal next time and nothing ping-pongs.
                File.Copy(source.FullName, target.FullName, overwrite: true);
                copied++;
            }
        }

        SaveState(folders.ToDictionary(
            Key,
            folder => Directory.EnumerateFiles(folder, SessionFilePattern).Select(Path.GetFileName).OfType<string>().ToList()));
        return new SharingResult(copied, deleted.Count);
    }

    private string Key(string folder) => Path.GetRelativePath(SessionsRoot, folder).ToLowerInvariant();

    private Dictionary<string, List<string>> LoadState()
    {
        if (!File.Exists(stateFile))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(stateFile)) ?? [];
        }
        catch (JsonException)
        {
            // Without history no deletions are inferred; the worst case is a deleted session coming back once.
            return [];
        }
    }

    private void SaveState(Dictionary<string, List<string>> state) =>
        AtomicFile.WriteAllText(stateFile, JsonSerializer.Serialize(state, JsonOptions));
}
