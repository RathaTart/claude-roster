using ClaudeRoster.Core;

namespace ClaudeRoster.Tests;

public sealed class UsageAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Resets = Now.AddHours(2);

    private readonly AccountProfile _work = Profile("Work");
    private readonly AccountProfile _personal = Profile("Personal");
    private readonly AccountProfile _spare = Profile("Spare");
    private readonly HashSet<string> _shown = [];

    [Fact]
    public void Running_low_suggests_the_account_with_the_most_left()
    {
        // Percent used: Personal has 70% and 40% left, Spare only 10% and 5%.
        var usage = Usage((_work, 85, 40), (_personal, 30, 60), (_spare, 90, 95));

        var alert = Check(usage);

        Assert.NotNull(alert);
        Assert.Equal("session", alert.Limit);
        Assert.Equal(15, alert.Left);
        Assert.Same(_personal, alert.SwitchTo);
        Assert.Equal(40, alert.SwitchToLeft);
        Assert.Equal("Work has 15% of its session limit left (resets in 2h). Personal has 40% left: click to switch.", alert.Message);
    }

    [Fact]
    public void Each_threshold_alerts_once_per_window()
    {
        Assert.NotNull(Check(Usage((_work, 85, 0))));
        Assert.Null(Check(Usage((_work, 86, 0))));

        var lower = Check(Usage((_work, 92, 0)));
        Assert.Equal(8, lower?.Left);
        Assert.Null(Check(Usage((_work, 95, 0))));
    }

    [Fact]
    public void A_jump_past_both_thresholds_alerts_once()
    {
        Assert.Equal(5, Check(Usage((_work, 95, 0)))?.Left);
        Assert.Null(Check(Usage((_work, 96, 0))));
    }

    [Fact]
    public void Plenty_left_or_an_old_reading_says_nothing()
    {
        Assert.Null(Check(Usage((_work, 50, 50))));

        var old = new Dictionary<string, UsageSample> { [_work.Id] = new(Now.AddHours(-1), 95, 0, Resets) };
        Assert.Null(UsageAlerts.Check(_work, [_work, _personal], old, _ => true, _shown, Now));
    }

    [Fact]
    public void An_account_without_a_saved_sign_in_isnt_suggested()
    {
        var usage = Usage((_work, 90, 0), (_personal, 0, 0));

        var alert = UsageAlerts.Check(_work, [_work, _personal], usage, profile => profile != _personal, _shown, Now);

        Assert.Null(alert?.SwitchTo);
        Assert.EndsWith("No other saved account to switch to.", alert?.Message);
    }

    [Fact]
    public void With_no_better_known_account_one_of_unknown_usage_is_worth_a_try()
    {
        var alert = Check(Usage((_work, 90, 0)));

        Assert.NotNull(alert?.SwitchTo);
        Assert.Null(alert.SwitchToLeft);
        Assert.EndsWith("Click to switch to Personal.", alert.Message);
    }

    private UsageAlert? Check(Dictionary<string, UsageSample> usage) =>
        UsageAlerts.Check(_work, [_work, _personal, _spare], usage, _ => true, _shown, Now);

    private static Dictionary<string, UsageSample> Usage(params (AccountProfile Profile, int Session, int Weekly)[] used) =>
        used.ToDictionary(entry => entry.Profile.Id, entry => new UsageSample(Now.AddMinutes(-2), entry.Session, entry.Weekly, Resets, Now.AddDays(3)));

    private static AccountProfile Profile(string alias) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Alias = alias,
        AccountUuid = Guid.NewGuid().ToString(),
    };
}
