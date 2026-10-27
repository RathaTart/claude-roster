using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

public sealed class PlanUsageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_account_gets_the_readings_from_while_it_was_signed_in()
    {
        using var temp = new TempDirectory();
        var work = Profile(FakeClaude.AccountA, Start);
        var personal = Profile(FakeClaude.AccountB, Start.AddHours(2));
        WriteHistory(temp.Root,
            Sample(Start.AddMinutes(-30), "org-old", """{"fh":90,"sd":90}"""),
            Sample(Start.AddMinutes(5), "org-work", """{"fh":10,"sd":40}"""),
            Sample(Start.AddMinutes(50), "org-work", """{"fh":22,"sd":45}"""),
            Sample(Start.AddHours(2).AddMinutes(1), "org-personal", "{}"));

        var usage = PlanUsage.ForProfiles(temp.Root, [work, personal]);

        Assert.Equal(new UsageSample(Start.AddMinutes(50), 22, 45), usage[work.Id]);
        Assert.False(usage[personal.Id].HasLimits);
    }

    [Fact]
    public void An_account_with_no_readings_is_left_out()
    {
        using var temp = new TempDirectory();
        var work = Profile(FakeClaude.AccountA, Start);
        WriteHistory(temp.Root, Sample(Start.AddMinutes(-5), "org", """{"fh":1,"sd":1}"""));

        Assert.Empty(PlanUsage.ForProfiles(temp.Root, [work]));
    }

    [Fact]
    public void A_missing_or_unreadable_file_means_no_usage()
    {
        using var temp = new TempDirectory();
        var work = Profile(FakeClaude.AccountA, Start);
        Assert.Empty(PlanUsage.ForProfiles(temp.Root, [work]));

        FakeClaude.Write(temp.Root, PlanUsage.FileName, "not json");
        Assert.Empty(PlanUsage.ForProfiles(temp.Root, [work]));
    }

    [Fact]
    public void Usage_left_resets_once_its_window_has_passed()
    {
        var sample = new UsageSample(Start, 70, 60);

        Assert.Equal(30, sample.SessionLeft(Start.AddHours(1)));
        Assert.Equal(100, sample.SessionLeft(Start.AddHours(5)));
        Assert.Equal(40, sample.WeeklyLeft(Start.AddDays(6)));
        Assert.Equal(100, sample.WeeklyLeft(Start.AddDays(7)));
        Assert.Null(new UsageSample(Start, null, null).SessionLeft(Start));
    }

    [Fact]
    public void Readings_say_when_they_were_taken_and_go_stale_after_a_few_minutes()
    {
        var sample = new UsageSample(Start, 7, 53);

        Assert.False(sample.IsStale(Start.AddMinutes(5)));
        Assert.True(sample.IsStale(Start.AddMinutes(6)));
        Assert.Equal("just now", sample.Taken(Start.AddSeconds(30), TimeZoneInfo.Utc));
        Assert.Equal("08:00, 36 min ago", sample.Taken(Start.AddMinutes(36), TimeZoneInfo.Utc));
        Assert.Equal("Tue 08:00, 2 days ago", sample.Taken(Start.AddDays(2), TimeZoneInfo.Utc));
        Assert.Equal("Oct 27, 9 days ago", sample.Taken(Start.AddDays(9), TimeZoneInfo.Utc));
        Assert.Equal("36m", sample.ShortAge(Start.AddMinutes(36)));
        Assert.Equal("3h", sample.ShortAge(Start.AddHours(3.5)));
        Assert.Equal("2d", sample.ShortAge(Start.AddDays(2)));
    }

    private static AccountProfile Profile(string account, DateTimeOffset lastUsed) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Alias = account[..4],
        AccountUuid = account,
        CreatedAt = lastUsed.AddDays(-1),
        LastUsedAt = lastUsed,
    };

    private static string Sample(DateTimeOffset at, string org, string usage) =>
        $$"""{"t":{{at.ToUnixTimeMilliseconds()}},"org":"{{org}}","u":{{usage}}}""";

    private static void WriteHistory(string dataRoot, params string[] samples) =>
        FakeClaude.Write(dataRoot, PlanUsage.FileName, $$"""{"version":2,"samples":[{{string.Join(",", samples)}}]}""");
}
