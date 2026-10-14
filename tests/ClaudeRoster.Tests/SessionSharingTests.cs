using ClaudeRoster.Core;
using static ClaudeRoster.Tests.FakeClaude;

namespace ClaudeRoster.Tests;

public sealed class SessionSharingTests : IDisposable
{
    private static readonly DateTime Earlier = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Earlier.AddHours(1);

    private readonly TempDirectory _temp = new();

    private string DataRoot => _temp.Combine("Claude");
    private SessionSharing Sharing => new(DataRoot, _temp.Combine("shared-sessions.json"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Sync_gives_both_accounts_every_session()
    {
        AddSession(DataRoot, AccountA, OrgA, "local_work.json", "work", Earlier);
        AddSession(DataRoot, AccountB, OrgB, "local_personal.json", "personal", Earlier);

        var result = Sharing.Sync([AccountA, AccountB]);

        Assert.Equal(2, result.Copied);
        Assert.Equal("personal", Read(DataRoot, SessionPath(AccountA, OrgA, "local_personal.json")));
        Assert.Equal("work", Read(DataRoot, SessionPath(AccountB, OrgB, "local_work.json")));
    }

    [Fact]
    public void Sync_keeps_the_most_recently_written_copy()
    {
        AddSession(DataRoot, AccountA, OrgA, "local_s.json", "old title", Earlier);
        AddSession(DataRoot, AccountB, OrgB, "local_s.json", "renamed", Later);

        Sharing.Sync([AccountA, AccountB]);
        var second = Sharing.Sync([AccountA, AccountB]);

        Assert.Equal("renamed", Read(DataRoot, SessionPath(AccountA, OrgA, "local_s.json")));
        Assert.Equal(0, second.Copied);
    }

    [Fact]
    public void A_session_deleted_in_one_account_is_deleted_in_the_other()
    {
        AddSession(DataRoot, AccountA, OrgA, "local_s.json", "s", Earlier);
        Sharing.Sync([AccountA, AccountB]); // B has no folder yet: nothing to share with
        AddSession(DataRoot, AccountB, OrgB, "local_other.json", "other", Earlier);
        Sharing.Sync([AccountA, AccountB]);
        Assert.True(Exists(DataRoot, SessionPath(AccountB, OrgB, "local_s.json")));

        File.Delete(Path.Combine(DataRoot, SessionPath(AccountB, OrgB, "local_s.json")));
        var result = Sharing.Sync([AccountA, AccountB]);

        Assert.Equal(1, result.Deleted);
        Assert.False(Exists(DataRoot, SessionPath(AccountA, OrgA, "local_s.json")));
        Assert.True(Exists(DataRoot, SessionPath(AccountA, OrgA, "local_other.json")));
    }

    [Fact]
    public void An_account_joining_later_does_not_delete_anything()
    {
        AddSession(DataRoot, AccountA, OrgA, "local_1.json", "1", Earlier);
        AddSession(DataRoot, AccountB, OrgB, "local_2.json", "2", Earlier);
        Sharing.Sync([AccountA, AccountB]);

        AddSession(DataRoot, AccountC, OrgB, "local_3.json", "3", Earlier);
        var result = Sharing.Sync([AccountA, AccountB, AccountC]);

        Assert.Equal(0, result.Deleted);
        Assert.True(Exists(DataRoot, SessionPath(AccountC, OrgB, "local_1.json")));
        Assert.True(Exists(DataRoot, SessionPath(AccountA, OrgA, "local_3.json")));
    }

    [Fact]
    public void Only_session_files_are_shared()
    {
        AddSession(DataRoot, AccountA, OrgA, "local_s.json", "s", Earlier);
        Write(DataRoot, Path.Combine(SessionSharing.SessionsFolderName, AccountA, OrgA, "scheduled-tasks.json"), "[\"work task\"]");
        AddSession(DataRoot, AccountB, OrgB, "local_t.json", "t", Earlier);

        Sharing.Sync([AccountA, AccountB]);

        Assert.False(Exists(DataRoot, Path.Combine(SessionSharing.SessionsFolderName, AccountB, OrgB, "scheduled-tasks.json")));
    }
}
