using ClaudeRoster.Core;
using static ClaudeRoster.Tests.FakeClaude;

namespace ClaudeRoster.Tests;

public sealed class AccountSwitcherTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeClaudeProcess _process = new();

    private string DataRoot => _temp.Combine("Claude");
    private RosterPaths Paths => new(_temp.Combine("Roster"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Switch_activates_the_target_and_carries_forward_the_account_it_leaves()
    {
        var switcher = CreateSwitcher();
        var (work, personal) = await EnrolTwoAccountsAsync(switcher);
        // Claude refreshes tokens while an account is in use; leaving it must keep the fresh copy.
        SignIn(DataRoot, AccountB, "B-refreshed");

        await switcher.SwitchAsync(work.Id);

        Assert.Equal(AccountA, ConfigString(DataRoot, "lastKnownAccountUuid"));
        Assert.Equal("cookies-A", Read(DataRoot, Cookies));
        Assert.Equal(work.Id, switcher.GetStatus().Profile?.Id);
        Assert.True(_process.Running);

        await switcher.SwitchAsync(personal.Id);

        Assert.Equal("cookies-B-refreshed", Read(DataRoot, Cookies));
        Assert.Equal("token-B-refreshed", ConfigString(DataRoot, "oauth:tokenCache"));
    }

    [Fact]
    public async Task An_account_keeps_the_colour_and_short_name_it_was_given()
    {
        var switcher = CreateSwitcher();
        var (work, _) = await EnrolTwoAccountsAsync(switcher);

        switcher.SetColor(work.Id, "#3e7cb1");
        switcher.SetShortName(work.Id, "  Work  ");

        var saved = switcher.ListProfiles().Single(profile => profile.Id == work.Id);
        Assert.Equal("#3E7CB1", saved.Color);
        Assert.Equal("Work", saved.ShortName);

        switcher.SetColor(work.Id, null);
        switcher.SetShortName(work.Id, " ");
        saved = switcher.ListProfiles().Single(profile => profile.Id == work.Id);
        Assert.Null(saved.Color);
        Assert.Null(saved.ShortName);

        Assert.Throws<ClaudeRosterException>(() => switcher.SetColor(work.Id, "blue"));
        Assert.Throws<ClaudeRosterException>(() => switcher.SetShortName(work.Id, "A name far too long"));
    }

    [Fact]
    public async Task Uninstalling_deletes_the_roster_data_and_leaves_Claude_signed_in()
    {
        var switcher = CreateSwitcher();
        await EnrolTwoAccountsAsync(switcher);

        switcher.DeleteAllData();

        Assert.False(Directory.Exists(Paths.Root));
        Assert.Equal(AccountB, ConfigString(DataRoot, "lastKnownAccountUuid"));
        Assert.Equal("cookies-B", Read(DataRoot, Cookies));
    }

    [Fact]
    public void Uninstalling_refuses_a_folder_that_isnt_the_rosters()
    {
        Write(_temp.Combine("Documents"), "notes.txt", "keep me");
        var switcher = new AccountSwitcher(new ClaudeInstall(DataRoot, null, null), new RosterPaths(_temp.Combine("Documents")), process: _process);

        Assert.Throws<ClaudeRosterException>(switcher.DeleteAllData);
        Assert.Equal("keep me", Read(_temp.Combine("Documents"), "notes.txt"));
    }

    [Fact]
    public async Task Switch_refuses_to_leave_an_unsaved_account_and_leaves_Claude_alone()
    {
        var switcher = CreateSwitcher();
        var (work, _) = await EnrolTwoAccountsAsync(switcher);
        await switcher.PrepareNewSignInAsync();
        SignIn(DataRoot, AccountC, "C");
        var closes = _process.Closes;

        await Assert.ThrowsAsync<UnsavedAccountException>(() => switcher.SwitchAsync(work.Id));

        Assert.Equal(closes, _process.Closes);
        Assert.Equal("cookies-C", Read(DataRoot, Cookies));
    }

    [Fact]
    public async Task Switch_puts_the_previous_account_back_when_activation_fails()
    {
        var snapshots = new FailingSnapshotter(new SessionSnapshotter());
        var switcher = CreateSwitcher(snapshots);
        var (work, _) = await EnrolTwoAccountsAsync(switcher);
        snapshots.FailNextApply();

        await Assert.ThrowsAsync<ClaudeRosterException>(() => switcher.SwitchAsync(work.Id));

        Assert.Equal("cookies-B", Read(DataRoot, Cookies));
        Assert.Equal(AccountB, ConfigString(DataRoot, "lastKnownAccountUuid"));
        Assert.True(_process.Running);
    }

    [Fact]
    public async Task Switch_to_the_signed_in_account_only_opens_Claude()
    {
        var switcher = CreateSwitcher();
        var (_, personal) = await EnrolTwoAccountsAsync(switcher);
        _process.Running = false;
        var closes = _process.Closes;

        await switcher.SwitchAsync(personal.Id);

        Assert.Equal(closes, _process.Closes);
        Assert.True(_process.Running);
        Assert.Equal("cookies-B", Read(DataRoot, Cookies));
    }

    [Fact]
    public async Task PrepareNewSignIn_signs_Claude_out_and_keeps_the_saved_account()
    {
        var switcher = CreateSwitcher();
        SignIn(DataRoot, AccountA, "A");
        var work = switcher.SaveCurrent("Work");

        await switcher.PrepareNewSignInAsync();

        Assert.Null(switcher.GetStatus().AccountUuid);
        Assert.False(Exists(DataRoot, Cookies));
        Assert.True(switcher.HasSavedSignIn(work));
        Assert.Equal(1, _process.Opens);
    }

    [Fact]
    public async Task Status_flags_tokens_without_an_account_id_as_unsafe_to_switch()
    {
        var switcher = CreateSwitcher();
        var (work, _) = await EnrolTwoAccountsAsync(switcher);
        var config = ReadConfig(DataRoot);
        config.Remove("lastKnownAccountUuid");
        WriteConfig(DataRoot, config);

        Assert.True(switcher.GetStatus().IsUnidentified);
        await Assert.ThrowsAsync<ClaudeRosterException>(() => switcher.SwitchAsync(work.Id));
        Assert.Equal("cookies-B", Read(DataRoot, Cookies));
    }

    [Fact]
    public async Task Remove_refuses_the_signed_in_account_and_deletes_the_others()
    {
        var switcher = CreateSwitcher();
        var (work, personal) = await EnrolTwoAccountsAsync(switcher);

        Assert.Throws<ClaudeRosterException>(() => switcher.Remove(personal.Id));
        switcher.Remove(work.Id);

        Assert.DoesNotContain(switcher.ListProfiles(), profile => profile.Id == work.Id);
        Assert.False(Directory.Exists(Paths.ProfileDirectory(work.Id)));
    }

    [Fact]
    public async Task SaveCurrent_rejects_a_name_that_is_already_used()
    {
        var switcher = CreateSwitcher();
        SignIn(DataRoot, AccountA, "A");
        switcher.SaveCurrent("Work");
        await switcher.PrepareNewSignInAsync();
        SignIn(DataRoot, AccountB, "B");

        Assert.Throws<ClaudeRosterException>(() => switcher.SaveCurrent(" work "));
    }

    [Fact]
    public async Task Old_backups_are_pruned()
    {
        var switcher = CreateSwitcher();
        var (work, personal) = await EnrolTwoAccountsAsync(switcher);

        for (var i = 0; i < 4; i++)
        {
            await switcher.SwitchAsync(work.Id);
            await switcher.SwitchAsync(personal.Id);
        }

        Assert.Equal(5, Directory.GetDirectories(Paths.BackupsDirectory).Length);
    }

    [Fact]
    public async Task Switching_shares_Code_sessions_only_when_turned_on()
    {
        var switcher = CreateSwitcher();
        var (work, personal) = await EnrolTwoAccountsAsync(switcher);
        var written = DateTime.UtcNow.AddMinutes(-5);
        AddSession(DataRoot, AccountA, OrgA, "local_work.json", "work", written);
        AddSession(DataRoot, AccountB, OrgB, "local_personal.json", "personal", written);

        await switcher.SwitchAsync(work.Id);
        Assert.False(Exists(DataRoot, SessionPath(AccountA, OrgA, "local_personal.json")));

        switcher.SetShareCodeSessions(true);
        var outcome = await switcher.SwitchAsync(personal.Id);

        Assert.Null(outcome.Warning);
        Assert.True(Exists(DataRoot, SessionPath(AccountA, OrgA, "local_personal.json")));
        Assert.True(Exists(DataRoot, SessionPath(AccountB, OrgB, "local_work.json")));
        Assert.True(switcher.ShareCodeSessions);
    }

    private AccountSwitcher CreateSwitcher(ISessionSnapshotter? snapshots = null) =>
        new(new ClaudeInstall(DataRoot, null, null), Paths, process: _process, snapshots: snapshots);

    /// <summary>The way a user enrols: save A, "Add account", sign in to B, save B. B ends up signed in.</summary>
    private async Task<(AccountProfile Work, AccountProfile Personal)> EnrolTwoAccountsAsync(AccountSwitcher switcher)
    {
        SignIn(DataRoot, AccountA, "A");
        var work = switcher.SaveCurrent("Work");
        await switcher.PrepareNewSignInAsync();
        SignIn(DataRoot, AccountB, "B");
        var personal = switcher.SaveCurrent("Personal");
        return (work, personal);
    }
}
