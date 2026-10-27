using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

/// <param name="At">When Claude took the reading.</param>
/// <param name="SessionUsed">Percent of the 5-hour limit used, when Claude reported one.</param>
/// <param name="WeeklyUsed">Percent of the 7-day limit used, when Claude reported one.</param>
/// <param name="SessionResetsAt">When the 5-hour window resets, where the reading says (the add-on's do).</param>
/// <param name="WeeklyResetsAt">When the 7-day window resets, where the reading says.</param>
public sealed record UsageSample(
    DateTimeOffset At,
    int? SessionUsed,
    int? WeeklyUsed,
    DateTimeOffset? SessionResetsAt = null,
    DateTimeOffset? WeeklyResetsAt = null)
{
    public static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// The desktop app records a reading only now and then (when it starts, for one), and the add-on only when Claude
    /// Code replies, so anything older than this may be well behind what Claude's own usage page shows.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    public bool HasLimits => SessionUsed is not null || WeeklyUsed is not null;

    public bool IsStale(DateTimeOffset now) => now - At > FreshFor;

    /// <summary>When Claude took the reading: "just now", "15:54, 36 min ago", "Mon 15:54, 2 days ago", "Oct 20, 9 days ago".</summary>
    public string Taken(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (now - At < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(At, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        // The rest of the UI is English, so day and month names are too.
        var format = local.Date == today ? "HH:mm" : today - local.Date < TimeSpan.FromDays(7) ? "ddd HH:mm" : "MMM d";
        var clock = local.ToString(format, CultureInfo.InvariantCulture);
        return $"{clock}, {Age(now)}";
    }

    /// <summary>
    /// Percent left now. A window that has reset since the reading is all left: at its reset time where the reading
    /// gives one, else once a whole window has passed.
    /// </summary>
    public int? SessionLeft(DateTimeOffset now) => Left(SessionUsed, HasReset(SessionResetsAt, SessionWindow, now));

    public int? WeeklyLeft(DateTimeOffset now) => Left(WeeklyUsed, HasReset(WeeklyResetsAt, WeeklyWindow, now));

    /// <summary>"Session 93% left · Week 47% left", for menus and the command line.</summary>
    public string Summary(DateTimeOffset now) =>
        HasLimits
            ? string.Join(" · ", new[] { ("Session", SessionLeft(now), SessionResetsIn(now)), ("Week", WeeklyLeft(now), WeeklyResetsIn(now)) }
                .Where(part => part.Item2 is not null)
                .Select(part => $"{part.Item1} {part.Item2}% left" + (part.Item3 is null ? "" : $" ↻{part.Item3}")))
            : "No usage limits reported";

    /// <summary>Time until the 5-hour window resets ("2h45m"), when the reading says and it hasn't passed.</summary>
    public string? SessionResetsIn(DateTimeOffset now) => Countdown(SessionResetsAt, now);

    public string? WeeklyResetsIn(DateTimeOffset now) => Countdown(WeeklyResetsAt, now);

    /// <summary>"45m", "2h45m", "3d4h", as cc-hud and Claude Code's status line write them.</summary>
    private static string? Countdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || reset <= now)
        {
            return null;
        }

        var left = reset - now;
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes < 60 ? $"{minutes}m"
            : minutes < 24 * 60 ? (minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60}m")
            : $"{minutes / (24 * 60)}d{minutes % (24 * 60) / 60}h";
    }

    /// <summary>"just now", "12 min ago", "3 h ago", "2 days ago".</summary>
    public string Age(DateTimeOffset now)
    {
        var age = now - At;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : (int)age.TotalDays == 1 ? "1 day ago"
            : $"{(int)age.TotalDays} days ago";
    }

    /// <summary>"36m", "2h", "3d": the age for tight spots like the title bar button.</summary>
    public string ShortAge(DateTimeOffset now)
    {
        var age = now - At;
        return age.TotalHours < 1 ? $"{Math.Max(1, (int)age.TotalMinutes)}m"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
            : $"{(int)age.TotalDays}d";
    }

    private bool HasReset(DateTimeOffset? resetsAt, TimeSpan window, DateTimeOffset now) =>
        resetsAt is { } reset ? now >= reset : now - At >= window;

    private static int? Left(int? used, bool reset) => used is null ? null : reset ? 100 : Math.Clamp(100 - used.Value, 0, 100);
}

/// <summary>
/// Reads plan usage from two places: plan-usage-history.json, where the desktop app records a reading now and then,
/// and the roster-usage Claude Code add-on's store, which saves the figures Claude Code has after every reply. Neither
/// says which account a reading is for, so each saved account gets the readings taken while it was the one signed in.
/// </summary>
public static class PlanUsage
{
    public const string FileName = "plan-usage-history.json";

    /// <summary>The add-on's store file: plugins\store\roster-usage_&lt;marketplace&gt;-&lt;hash&gt;.json.</summary>
    public const string AddOnStorePattern = "roster-usage_*.json";

    /// <summary>Claude Code's plugin stores: ~\.claude\plugins\store, or under CLAUDE_CONFIG_DIR when set.</summary>
    public static string DefaultAddOnStoreFolder()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        return Path.Combine(root, "plugins", "store");
    }

    /// <summary>The newest reading for each profile that has one, keyed by profile id.</summary>
    /// <param name="addOnStoreFolder">Where the add-on's store lives; null leaves it out.</param>
    public static IReadOnlyDictionary<string, UsageSample> ForProfiles(
        string dataRoot, IReadOnlyList<AccountProfile> profiles, string? addOnStoreFolder = null)
    {
        var readings = LoadHistory(dataRoot);
        if (addOnStoreFolder is not null && LoadAddOn(addOnStoreFolder) is { } latest)
        {
            readings.Add(latest);
        }

        readings.Sort((a, b) => a.At.CompareTo(b.At));
        var result = new Dictionary<string, UsageSample>();
        if (readings.Count == 0)
        {
            return result;
        }

        // A profile was signed in from its LastUsedAt until the next profile's; Claude reopens on it after each switch
        // and takes a reading then.
        var starts = profiles
            .Select(profile => (Profile: profile, From: profile.LastUsedAt ?? profile.CreatedAt))
            .OrderBy(entry => entry.From)
            .ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i].From;
            var until = i + 1 < starts.Count ? starts[i + 1].From : DateTimeOffset.MaxValue;
            var newest = readings.LastOrDefault(reading => reading.At >= from && reading.At < until);
            if (newest is not null)
            {
                result[starts[i].Profile.Id] = newest;
            }
        }

        return result;
    }

    private static List<UsageSample> LoadHistory(string dataRoot)
    {
        var readings = new List<UsageSample>();
        if (Parse(Path.Combine(dataRoot, FileName)) is not JsonObject root || root["samples"] is not JsonArray samples)
        {
            return readings;
        }

        foreach (var sample in samples.OfType<JsonObject>())
        {
            if (sample["t"] is JsonValue time && time.TryGetValue<long>(out var milliseconds) && At(milliseconds) is { } at)
            {
                var usage = sample["u"] as JsonObject;
                readings.Add(new UsageSample(at, Percent(usage?["fh"]), Percent(usage?["sd"])));
            }
        }

        return readings;
    }

    /// <summary>The add-on's <c>latest</c>: <c>{ at, limits: [{ kind, percentUsed, resetsAt? }] }</c>.</summary>
    private static UsageSample? LoadAddOn(string storeFolder)
    {
        string? newest;
        try
        {
            // One store per marketplace it was installed from; the most recently written has the latest figures.
            newest = Directory.Exists(storeFolder)
                ? Directory.EnumerateFiles(storeFolder, AddOnStorePattern).MaxBy(File.GetLastWriteTimeUtc)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (newest is null || Parse(newest)?["latest"] is not JsonObject latest
            || latest["at"] is not JsonValue time || !time.TryGetValue<long>(out var milliseconds) || At(milliseconds) is not { } at
            || latest["limits"] is not JsonArray limits)
        {
            return null;
        }

        (int? Used, DateTimeOffset? Resets) Window(string kind)
        {
            var limit = limits.OfType<JsonObject>()
                .FirstOrDefault(limit => limit["kind"] is JsonValue name && name.TryGetValue<string>(out var text) && text == kind);
            if (limit is null)
            {
                return (null, null);
            }

            DateTimeOffset? resets = limit["resetsAt"] is JsonValue value && value.TryGetValue<string>(out var raw)
                                     && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            return (Percent(limit["percentUsed"]), resets);
        }

        var session = Window("five_hour");
        var weekly = Window("seven_day");
        return session.Used is null && weekly.Used is null
            ? null
            : new UsageSample(at, session.Used, weekly.Used, session.Resets, weekly.Resets);
    }

    private static JsonNode? Parse(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Claude may be rewriting it; never ask for an exclusive handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Usage is a nicety: a file being rewritten, or one in a new format, just shows no usage.
            return null;
        }
    }

    private static DateTimeOffset? At(long milliseconds) =>
        milliseconds is > 0 and < 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    private static int? Percent(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && number is >= 0 and <= 1000
            ? (int)Math.Round(number)
            : null;
}
