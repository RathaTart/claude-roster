using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

public sealed class UsageHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_day_shows_its_highest_session_use()
    {
        using var temp = new TempDirectory();
        var history = new UsageHistory(temp.Combine("usage-history.json"));

        history.Record(Readings("work",
            (Now.AddDays(-2).AddHours(-3), 40), (Now.AddDays(-2), 85), (Now.AddDays(-2).AddHours(1), 10),
            (Now.AddHours(-1), 20), (Now, 35)), Now);

        var peaks = history.DailyPeaks(Now, TimeZoneInfo.Utc)["work"];
        Assert.Equal([null, null, null, null, 85, null, 35], peaks);
    }

    [Fact]
    public void Recording_the_same_readings_again_adds_nothing_and_old_ones_drop_out()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("usage-history.json");
        var history = new UsageHistory(file);
        history.Record(Readings("work", (Now.AddDays(-9), 99), (Now.AddDays(-1), 50)), Now);
        var written = File.GetLastWriteTimeUtc(file);

        history.Record(Readings("work", (Now.AddDays(-1), 50)), Now);

        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Equal([null, null, null, null, null, 50, null], history.DailyPeaks(Now, TimeZoneInfo.Utc)["work"]);
    }

    [Fact]
    public void Readings_without_limits_and_a_broken_file_are_ignored()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("usage-history.json");
        File.WriteAllText(file, "not json");
        var history = new UsageHistory(file);

        history.Record(new Dictionary<string, List<UsageSample>> { ["free"] = [new UsageSample(Now, null, null)] }, Now);

        Assert.Empty(history.DailyPeaks(Now, TimeZoneInfo.Utc));
    }

    private static Dictionary<string, List<UsageSample>> Readings(string profile, params (DateTimeOffset At, int Session)[] readings) =>
        new() { [profile] = readings.Select(reading => new UsageSample(reading.At, reading.Session, 30)).ToList() };
}
