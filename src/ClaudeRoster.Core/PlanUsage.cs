using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoster.Core;

/// <param name="At">When Claude took the reading.</param>
/// <param name="SessionUsed">Percent of the 5-hour limit used, when Claude reported one.</param>
/// <param name="WeeklyUsed">Percent of the 7-day limit used, when Claude reported one.</param>
public sealed record UsageSample(DateTimeOffset At, int? SessionUsed, int? WeeklyUsed)
{
    public static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Claude only records a reading now and then (when it starts, for one), so anything older than this may be well
    /// behind what Claude's own usage page shows.
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

    /// <summary>Percent left now. A reading older than its window has reset since, so it's all left.</summary>
    public int? SessionLeft(DateTimeOffset now) => Left(SessionUsed, now - At >= SessionWindow);

    public int? WeeklyLeft(DateTimeOffset now) => Left(WeeklyUsed, now - At >= WeeklyWindow);

    /// <summary>"Session 93% left · Week 47% left", for menus and the command line.</summary>
    public string Summary(DateTimeOffset now) =>
        HasLimits
            ? string.Join(" · ", new[] { ("Session", SessionLeft(now)), ("Week", WeeklyLeft(now)) }
                .Where(part => part.Item2 is not null)
                .Select(part => $"{part.Item1} {part.Item2}% left"))
            : "No usage limits reported";

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

    private static int? Left(int? used, bool reset) => used is null ? null : reset ? 100 : Math.Clamp(100 - used.Value, 0, 100);
}

/// <summary>
/// Reads the plan usage Claude records in plan-usage-history.json. Readings are keyed by organization, not account,
/// so each saved account gets the readings taken while it was the one signed in.
/// </summary>
public static class PlanUsage
{
    public const string FileName = "plan-usage-history.json";

    private sealed record Reading(DateTimeOffset At, string Org, int? SessionUsed, int? WeeklyUsed);

    /// <summary>The newest reading for each profile that has one, keyed by profile id.</summary>
    public static IReadOnlyDictionary<string, UsageSample> ForProfiles(string dataRoot, IReadOnlyList<AccountProfile> profiles)
    {
        var readings = Load(dataRoot);
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
                result[starts[i].Profile.Id] = new UsageSample(newest.At, newest.SessionUsed, newest.WeeklyUsed);
            }
        }

        return result;
    }

    private static List<Reading> Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream) is not JsonObject root || root["samples"] is not JsonArray samples)
            {
                return [];
            }

            var readings = new List<Reading>();
            foreach (var sample in samples.OfType<JsonObject>())
            {
                if (sample["t"] is JsonValue time && time.TryGetValue<long>(out var milliseconds)
                    && sample["org"] is JsonValue org && org.TryGetValue<string>(out var orgId))
                {
                    var usage = sample["u"] as JsonObject;
                    readings.Add(new Reading(
                        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), orgId, Percent(usage?["fh"]), Percent(usage?["sd"])));
                }
            }

            return readings.OrderBy(reading => reading.At).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            // Usage is a nicety: a file Claude is rewriting, or one in a new format, just shows no usage.
            return [];
        }
    }

    private static int? Percent(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && number is >= 0 and <= 1000
            ? (int)Math.Round(number)
            : null;
}
