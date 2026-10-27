namespace ClaudeRoster.Core;

/// <param name="Key">Identifies the window and threshold, so each alerts once.</param>
/// <param name="Limit">"session" or "weekly".</param>
/// <param name="Left">Percent of that limit left.</param>
/// <param name="ResetsIn">Time until it resets ("2h45m"), when known.</param>
/// <param name="SwitchTo">The saved account to suggest instead, if there's one worth suggesting.</param>
/// <param name="SwitchToLeft">The least it has left of either limit, when its usage is known.</param>
public sealed record UsageAlert(
    string Key,
    AccountProfile Account,
    string Limit,
    int Left,
    string? ResetsIn,
    AccountProfile? SwitchTo,
    int? SwitchToLeft)
{
    public string Message
    {
        get
        {
            var resets = ResetsIn is null ? "" : $" (resets in {ResetsIn})";
            var head = $"{Account.Alias} has {Left}% of its {Limit} limit left{resets}.";
            return SwitchTo switch
            {
                null => $"{head} No other saved account to switch to.",
                _ when SwitchToLeft is { } left => $"{head} {SwitchTo.Alias} has {left}% left: click to switch.",
                _ => $"{head} Click to switch to {SwitchTo.Alias}.",
            };
        }
    }
}

/// <summary>Decides when to warn that the signed-in account is running low, and which account to suggest.</summary>
public static class UsageAlerts
{
    /// <summary>Percent left at which to warn; each is shown once per window.</summary>
    public static readonly IReadOnlyList<int> Thresholds = [20, 10];

    /// <summary>Older readings could be well out of date; better to say nothing than to cry wolf.</summary>
    public static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(30);

    /// <param name="usage">The latest reading for each account, keyed by profile id.</param>
    /// <param name="canSwitchTo">Whether an account has a saved sign-in to switch to.</param>
    /// <param name="shown">Keys already alerted; the new alert's keys are added.</param>
    public static UsageAlert? Check(
        AccountProfile signedIn,
        IReadOnlyList<AccountProfile> profiles,
        IReadOnlyDictionary<string, UsageSample> usage,
        Func<AccountProfile, bool> canSwitchTo,
        ISet<string> shown,
        DateTimeOffset now)
    {
        if (!usage.TryGetValue(signedIn.Id, out var sample) || now - sample.At > MaxReadingAge)
        {
            return null;
        }

        var limits = new[]
        {
            ("session", sample.SessionLeft(now), sample.SessionResetsIn(now), WindowId(sample.SessionResetsAt, sample.At, "yyyyMMddHH")),
            ("weekly", sample.WeeklyLeft(now), sample.WeeklyResetsIn(now), WindowId(sample.WeeklyResetsAt, sample.At, "yyyyMMdd")),
        };
        foreach (var (limit, left, resetsIn, window) in limits)
        {
            if (left is not { } leftNow)
            {
                continue;
            }

            // Every threshold this reading is under; alert at the lowest, and count the higher ones as said, so a jump
            // straight past 20% to 8% warns once, not twice.
            var crossed = Thresholds.Where(threshold => leftNow <= threshold).Select(threshold => $"{signedIn.Id}:{limit}:{window}:{threshold}").ToList();
            if (crossed.Count == 0 || shown.Contains(crossed[^1]))
            {
                continue;
            }

            foreach (var key in crossed)
            {
                shown.Add(key);
            }

            var (switchTo, switchToLeft) = BestAlternative(signedIn, profiles, usage, canSwitchTo, leftNow, now);
            return new UsageAlert(crossed[^1], signedIn, limit, leftNow, resetsIn, switchTo, switchToLeft);
        }

        return null;
    }

    /// <summary>
    /// The account with the most left of its tighter limit, if that beats the signed-in one; failing that, one whose
    /// usage isn't known, which is worth a try.
    /// </summary>
    private static (AccountProfile?, int?) BestAlternative(
        AccountProfile signedIn,
        IReadOnlyList<AccountProfile> profiles,
        IReadOnlyDictionary<string, UsageSample> usage,
        Func<AccountProfile, bool> canSwitchTo,
        int left,
        DateTimeOffset now)
    {
        var others = profiles.Where(profile => profile.Id != signedIn.Id && canSwitchTo(profile)).ToList();
        var known = others
            .Select(profile => (Profile: profile, Left: usage.TryGetValue(profile.Id, out var other) && other.HasLimits
                ? Math.Min(other.SessionLeft(now) ?? 100, other.WeeklyLeft(now) ?? 100)
                : (int?)null))
            .ToList();
        var best = known.Where(entry => entry.Left > left).OrderByDescending(entry => entry.Left).FirstOrDefault();
        if (best.Profile is not null)
        {
            return (best.Profile, best.Left);
        }

        var unknown = known.FirstOrDefault(entry => entry.Left is null);
        return (unknown.Profile, null);
    }

    /// <summary>Which window a reading is in: its reset time where known, else the hour or day it was taken.</summary>
    private static string WindowId(DateTimeOffset? resetsAt, DateTimeOffset at, string bucket) =>
        resetsAt?.ToUnixTimeSeconds().ToString() ?? at.UtcDateTime.ToString(bucket, System.Globalization.CultureInfo.InvariantCulture);
}
