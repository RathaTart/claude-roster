using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

public sealed record SnapshotInfo(int Format, DateTimeOffset CreatedAt, string? AccountUuid);

public sealed record SessionEntryUsage(string RelativePath, bool Exists, long Bytes);

public interface ISessionSnapshotter
{
    SnapshotInfo? ReadInfo(string snapshotDirectory);
    void Capture(string dataRoot, string snapshotDirectory);
    void Apply(string snapshotDirectory, string dataRoot);
    void ClearSignIn(string dataRoot);
}

/// <summary>
/// Copies the bounded set of Claude desktop storage that carries a claude.ai sign-in into and out of the data
/// folder. Claude must be closed: Chromium holds these files open while it runs.
/// </summary>
public sealed class SessionSnapshotter(TimeProvider? time = null) : ISessionSnapshotter
{
    public const int Format = 1;

    private const string InfoFileName = "snapshot.json";
    private const string ConfigKeysFileName = "config-keys.json";
    private const string FilesDirectoryName = "files";
    private const string LocalState = "Local State";

    /// <summary>
    /// Chromium storage tied to the signed-in account. "Local State" holds the key that encrypts the cookies and
    /// token caches, so every snapshot carries the copy its data was encrypted with.
    /// </summary>
    public static readonly IReadOnlyList<string> Entries =
    [
        LocalState,
        @"Network\Cookies",
        @"Network\Cookies-journal",
        "Local Storage",
        "Session Storage",
        "IndexedDB",
        "WebStorage",
        "blob_storage",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public SnapshotInfo? ReadInfo(string snapshotDirectory)
    {
        var path = Path.Combine(snapshotDirectory, InfoFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(path), JsonOptions);
            return info?.Format == Format ? info : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Capture(string dataRoot, string snapshotDirectory)
    {
        var config = ClaudeConfig.Load(dataRoot);
        var staging = $"{snapshotDirectory}.tmp-{Guid.NewGuid():N}";
        var previous = snapshotDirectory + ".previous";
        try
        {
            var files = Path.Combine(staging, FilesDirectoryName);
            Directory.CreateDirectory(files);
            foreach (var entry in Entries)
            {
                CopyEntry(dataRoot, files, entry);
            }

            File.WriteAllText(
                Path.Combine(staging, ConfigKeysFileName),
                ClaudeConfig.ExtractAccountKeys(config).ToJsonString(JsonOptions));
            var info = new SnapshotInfo(Format, _time.GetUtcNow(), ClaudeConfig.ReadAccountUuid(config));
            File.WriteAllText(Path.Combine(staging, InfoFileName), JsonSerializer.Serialize(info, JsonOptions));

            // Swap the finished copy in, so a failure part-way never leaves a half-written snapshot behind.
            FileOps.DeleteDirectory(previous);
            if (Directory.Exists(snapshotDirectory))
            {
                FileOps.MoveDirectory(snapshotDirectory, previous);
            }

            try
            {
                FileOps.MoveDirectory(staging, snapshotDirectory);
            }
            catch
            {
                if (!Directory.Exists(snapshotDirectory) && Directory.Exists(previous))
                {
                    FileOps.MoveDirectory(previous, snapshotDirectory);
                }

                throw;
            }

            FileOps.DeleteDirectory(previous);
        }
        finally
        {
            FileOps.DeleteDirectory(staging);
        }
    }

    public void Apply(string snapshotDirectory, string dataRoot)
    {
        if (ReadInfo(snapshotDirectory) is null)
        {
            throw new ClaudeRosterException("The saved sign-in is missing or was made by an unsupported version.");
        }

        var accountKeys = JsonNode.Parse(File.ReadAllText(Path.Combine(snapshotDirectory, ConfigKeysFileName))) as JsonObject
                          ?? throw new InvalidDataException("The saved sign-in's config-keys.json is not a JSON object.");
        // Load before touching any files so an unreadable config fails while nothing has changed yet.
        var config = ClaudeConfig.Load(dataRoot);

        var files = Path.Combine(snapshotDirectory, FilesDirectoryName);
        foreach (var entry in Entries)
        {
            // Without its own key the snapshot's cookies are unreadable anyway; keep the live key rather than lose it.
            if (entry == LocalState && !EntryExists(files, entry))
            {
                continue;
            }

            DeleteEntry(dataRoot, entry);
            CopyEntry(files, dataRoot, entry);
        }

        ClaudeConfig.ReplaceAccountKeys(config, accountKeys);
        ClaudeConfig.Save(dataRoot, config);
    }

    /// <summary>Leaves Claude on its sign-in screen. Local State stays: it only holds the device's encryption key.</summary>
    public void ClearSignIn(string dataRoot)
    {
        var config = ClaudeConfig.Load(dataRoot);
        foreach (var entry in Entries.Where(entry => entry != LocalState))
        {
            DeleteEntry(dataRoot, entry);
        }

        ClaudeConfig.ReplaceAccountKeys(config, null);
        ClaudeConfig.Save(dataRoot, config);
    }

    public static IReadOnlyList<SessionEntryUsage> Describe(string dataRoot) =>
        Entries.Select(entry =>
        {
            var path = Path.Combine(dataRoot, entry);
            if (File.Exists(path))
            {
                return new SessionEntryUsage(entry, true, new FileInfo(path).Length);
            }

            if (!Directory.Exists(path))
            {
                return new SessionEntryUsage(entry, false, 0);
            }

            var bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
            return new SessionEntryUsage(entry, true, bytes);
        }).ToList();

    private static bool EntryExists(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static void CopyEntry(string sourceRoot, string destinationRoot, string relativePath)
    {
        var source = Path.Combine(sourceRoot, relativePath);
        var destination = Path.Combine(destinationRoot, relativePath);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            return;
        }

        if (Directory.Exists(source))
        {
            FileOps.CopyDirectory(source, destination);
        }
    }

    private static void DeleteEntry(string root, string relativePath)
    {
        var target = Path.Combine(root, relativePath);
        if (File.Exists(target))
        {
            FileOps.DeleteFile(target);
            return;
        }

        FileOps.DeleteDirectory(target);
    }
}
