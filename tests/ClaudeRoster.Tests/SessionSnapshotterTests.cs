using ClaudeRoster.Core;
using static ClaudeRoster.Tests.FakeClaude;

namespace ClaudeRoster.Tests;

public sealed class SessionSnapshotterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SessionSnapshotter _snapshots = new();

    private string DataRoot => _temp.Combine("Claude");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Apply_restores_the_captured_account_and_keeps_device_settings()
    {
        SignIn(DataRoot, AccountA, "A");
        var snapshot = _temp.Combine("snapshot-a");
        _snapshots.Capture(DataRoot, snapshot);

        _snapshots.ClearSignIn(DataRoot);
        SignIn(DataRoot, AccountB, "B");
        var config = ReadConfig(DataRoot);
        config["locale"] = "th-TH";
        WriteConfig(DataRoot, config);

        _snapshots.Apply(snapshot, DataRoot);

        Assert.Equal("cookies-A", Read(DataRoot, Cookies));
        Assert.Equal("local-A", Read(DataRoot, LocalStorageLog));
        Assert.Equal(AccountA, ConfigString(DataRoot, "lastKnownAccountUuid"));
        Assert.Equal("token-A", ConfigString(DataRoot, "oauth:tokenCache"));
        Assert.Equal("token2-A", ConfigString(DataRoot, "oauth:tokenCacheV2"));
        Assert.Equal("th-TH", ConfigString(DataRoot, "locale"));
    }

    [Fact]
    public void Apply_removes_account_data_the_snapshot_does_not_have()
    {
        SignIn(DataRoot, AccountA, "A");
        var snapshot = _temp.Combine("snapshot-a");
        _snapshots.Capture(DataRoot, snapshot);

        Write(DataRoot, @"Session Storage\000001.log", "session-B");
        var config = ReadConfig(DataRoot);
        config["oauth:tokenCacheV3"] = "token3-B";
        WriteConfig(DataRoot, config);

        _snapshots.Apply(snapshot, DataRoot);

        Assert.False(Exists(DataRoot, "Session Storage"));
        Assert.Null(ReadConfig(DataRoot)["oauth:tokenCacheV3"]);
    }

    [Fact]
    public void ClearSignIn_signs_out_but_keeps_the_encryption_key_and_device_settings()
    {
        SignIn(DataRoot, AccountA, "A");

        _snapshots.ClearSignIn(DataRoot);

        Assert.False(Exists(DataRoot, Cookies));
        Assert.False(Exists(DataRoot, "Local Storage"));
        Assert.False(Exists(DataRoot, "IndexedDB"));
        Assert.True(Exists(DataRoot, "Local State"));
        var config = ReadConfig(DataRoot);
        Assert.Empty(ClaudeConfig.AccountKeyNames(config));
        Assert.Equal("en-US", (string?)config["locale"]);
    }

    [Fact]
    public void Capture_replaces_an_earlier_snapshot_and_leaves_no_temporary_folders()
    {
        SignIn(DataRoot, AccountA, "A");
        var snapshot = _temp.Combine("snapshot-a");
        _snapshots.Capture(DataRoot, snapshot);
        SignIn(DataRoot, AccountA, "A-refreshed");

        _snapshots.Capture(DataRoot, snapshot);

        Assert.Equal(AccountA, _snapshots.ReadInfo(snapshot)?.AccountUuid);
        Assert.Equal("cookies-A-refreshed", Read(snapshot, Path.Combine("files", Cookies)));
        Assert.Equal(["snapshot-a"], Directory.GetDirectories(_temp.Root, "snapshot-a*").Select(Path.GetFileName));
    }

    [Fact]
    public void Apply_refuses_an_incomplete_snapshot_without_touching_Claude()
    {
        SignIn(DataRoot, AccountA, "A");
        var broken = _temp.Combine("broken");
        Directory.CreateDirectory(broken);

        Assert.Throws<ClaudeRosterException>(() => _snapshots.Apply(broken, DataRoot));
        Assert.Equal("cookies-A", Read(DataRoot, Cookies));
        Assert.Equal(AccountA, ConfigString(DataRoot, "lastKnownAccountUuid"));
    }
}
