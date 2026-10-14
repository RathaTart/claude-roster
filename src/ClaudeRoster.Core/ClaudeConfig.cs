using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

/// <summary>
/// Reads and edits the desktop app's config.json (an electron-store file). Most of it is device-wide; only the
/// keys matched by <see cref="IsAccountKey"/> belong to the signed-in account.
/// </summary>
public static class ClaudeConfig
{
    public const string FileName = "config.json";
    public const string AccountUuidKey = "lastKnownAccountUuid";

    private const string TokenKeyPrefix = "oauth:";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>oauth:* hold the encrypted token caches; bootSidebarPlaceholder caches the account's sidebar.</summary>
    public static bool IsAccountKey(string key) =>
        key.StartsWith(TokenKeyPrefix, StringComparison.Ordinal)
        || key is AccountUuidKey or "bootSidebarPlaceholder";

    public static JsonObject Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, FileName);
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        // Claude may be running and rewriting the file, so never ask for an exclusive handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream) as JsonObject
               ?? throw new InvalidDataException("Claude's config.json is not a JSON object.");
    }

    public static void Save(string dataRoot, JsonObject config) =>
        AtomicFile.WriteAllText(Path.Combine(dataRoot, FileName), config.ToJsonString(WriteOptions));

    /// <summary>The signed-in account's id, or null when Claude is signed out.</summary>
    public static string? ReadAccountUuid(JsonObject config) =>
        config[AccountUuidKey] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && Guid.TryParse(text, out _)
            ? text
            : null;

    public static bool HasTokens(JsonObject config) =>
        config.Any(pair => pair.Key.StartsWith(TokenKeyPrefix, StringComparison.Ordinal) && pair.Value is not null);

    public static IReadOnlyList<string> AccountKeyNames(JsonObject config) =>
        config.Select(pair => pair.Key).Where(IsAccountKey).ToList();

    public static JsonObject ExtractAccountKeys(JsonObject config)
    {
        var result = new JsonObject();
        foreach (var (key, value) in config)
        {
            if (IsAccountKey(key))
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>Drops every account key, then adds the ones in <paramref name="accountKeys"/>, if any.</summary>
    public static void ReplaceAccountKeys(JsonObject config, JsonObject? accountKeys)
    {
        foreach (var key in AccountKeyNames(config))
        {
            config.Remove(key);
        }

        if (accountKeys is null)
        {
            return;
        }

        foreach (var (key, value) in accountKeys)
        {
            if (IsAccountKey(key))
            {
                config[key] = value?.DeepClone();
            }
        }
    }
}
