using System.Text.Json;

namespace ClaudeRoster.Core;

/// <summary>
/// The roster's own log of plan usage readings per account, usage-history.json: Claude keeps few readings and the
/// add-on only the latest, so this is what a week of history is drawn from. Times and percentages only.
/// </summary>
public sealed class UsageHistory(string file)
{
    public const int Days = 7;

    /// <summary>A day more than is drawn, so the oldest day shown is whole whatever the time zone.</summary>
    private static readonly TimeSpan Kept = TimeSpan.FromDays(Days + 1);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Entry(string Profile, long At, int? Session, int? Weekly);

    private sealed class Log
    {
        public int Version { get; set; } = 1;
        public List<Entry> Readings { get; set; } = [];
    }

    /// <summary>Adds the readings not yet logged, keyed by profile id, and drops those older than a week.</summary>
    public void Record(IReadOnlyDictionary<string, List<UsageSample>> readings, DateTimeOffset now)
    {
        var log = Load();
        var known = log.Readings.Select(entry => (entry.Profile, entry.At)).ToHashSet();
        var changed = false;
        foreach (var (profile, samples) in readings)
        {
            foreach (var sample in samples.Where(sample => sample.HasLimits && now - sample.At <= Kept))
            {
                var at = sample.At.ToUnixTimeMilliseconds();
                if (known.Add((profile, at)))
                {
                    log.Readings.Add(new Entry(profile, at, sample.SessionUsed, sample.WeeklyUsed));
                    changed = true;
                }
            }
        }

        var cutoff = (now - Kept).ToUnixTimeMilliseconds();
        changed |= log.Readings.RemoveAll(entry => entry.At < cutoff) > 0;
        if (changed)
        {
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(log, Options));
        }
    }

    /// <summary>For each account: its highest session use on each of the last 7 days (today last), null where unknown.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int?>> DailyPeaks(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var first = today.AddDays(-(Days - 1));
        return Load().Readings
            .Where(entry => entry.Session is not null)
            .GroupBy(entry => entry.Profile)
            .ToDictionary(group => group.Key, group =>
            {
                var peaks = new int?[Days];
                foreach (var entry in group)
                {
                    var day = (TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(entry.At), zone).Date - first).Days;
                    if (day is >= 0 and < Days)
                    {
                        peaks[day] = Math.Max(peaks[day] ?? 0, entry.Session!.Value);
                    }
                }

                return (IReadOnlyList<int?>)peaks;
            });
    }

    private Log Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Log>(File.ReadAllText(file), Options) ?? new Log() : new Log();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A history is a nicety: an unreadable one starts over rather than stopping anything.
            return new Log();
        }
    }
}
