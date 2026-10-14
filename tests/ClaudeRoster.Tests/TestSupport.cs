using System.Text.Json.Nodes;
using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClaudeRosterTests", Guid.NewGuid().ToString("N"));

    public string Combine(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Builds a data folder shaped like the Claude desktop app's, with markers instead of real secrets.</summary>
internal static class FakeClaude
{
    public const string AccountA = "aaaaaaaa-1111-4111-8111-111111111111";
    public const string AccountB = "bbbbbbbb-2222-4222-8222-222222222222";
    public const string AccountC = "cccccccc-3333-4333-8333-333333333333";

    public const string Cookies = @"Network\Cookies";
    public const string LocalStorageLog = @"Local Storage\leveldb\000003.log";

    public static void SignIn(string dataRoot, string accountUuid, string marker)
    {
        Write(dataRoot, "Local State", """{"os_crypt":{"encrypted_key":"device-key"}}""");
        Write(dataRoot, Cookies, $"cookies-{marker}");
        Write(dataRoot, LocalStorageLog, $"local-{marker}");
        Write(dataRoot, @"IndexedDB\https_claude.ai_0.indexeddb.leveldb\CURRENT", $"idb-{marker}");

        var config = ReadConfig(dataRoot);
        config["lastKnownAccountUuid"] = accountUuid;
        config["oauth:tokenCache"] = $"token-{marker}";
        config["oauth:tokenCacheV2"] = $"token2-{marker}";
        config["bootSidebarPlaceholder"] = new JsonArray($"chat-{marker}");
        config["locale"] ??= "en-US";
        WriteConfig(dataRoot, config);
    }

    public static string Read(string dataRoot, string relativePath) => File.ReadAllText(Path.Combine(dataRoot, relativePath));

    public static bool Exists(string dataRoot, string relativePath)
    {
        var path = Path.Combine(dataRoot, relativePath);
        return File.Exists(path) || Directory.Exists(path);
    }

    public static void Write(string dataRoot, string relativePath, string contents)
    {
        var path = Path.Combine(dataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public static JsonObject ReadConfig(string dataRoot) => ClaudeConfig.Load(dataRoot);

    public static void WriteConfig(string dataRoot, JsonObject config) =>
        Write(dataRoot, ClaudeConfig.FileName, config.ToJsonString());

    public static string? ConfigString(string dataRoot, string key) => (string?)ReadConfig(dataRoot)[key];
}

internal sealed class FakeClaudeProcess : IClaudeProcess
{
    public bool Running { get; set; } = true;
    public int Closes { get; private set; }
    public int Opens { get; private set; }

    public ClaudeRunState Capture() =>
        Running ? new ClaudeRunState(true, null, "AnthropicPBC.Claude_test!Claude", 0) : ClaudeRunState.NotRunning;

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Closes++;
        Running = false;
        return Task.CompletedTask;
    }

    public Task OpenAsync(ClaudeRunState previous, CancellationToken cancellationToken = default)
    {
        Opens++;
        Running = true;
        return Task.CompletedTask;
    }

    public bool IsDataRootInUse() => Running;
}

/// <summary>Fails the next Apply after wrecking part of the live folder, like a disk error mid-copy would.</summary>
internal sealed class FailingSnapshotter(ISessionSnapshotter inner) : ISessionSnapshotter
{
    private bool _failNextApply;

    public void FailNextApply() => _failNextApply = true;

    public SnapshotInfo? ReadInfo(string snapshotDirectory) => inner.ReadInfo(snapshotDirectory);

    public void Capture(string dataRoot, string snapshotDirectory) => inner.Capture(dataRoot, snapshotDirectory);

    public void ClearSignIn(string dataRoot) => inner.ClearSignIn(dataRoot);

    public void Apply(string snapshotDirectory, string dataRoot)
    {
        if (!_failNextApply)
        {
            inner.Apply(snapshotDirectory, dataRoot);
            return;
        }

        _failNextApply = false;
        File.Delete(Path.Combine(dataRoot, FakeClaude.Cookies));
        throw new IOException("Simulated disk error.");
    }
}
