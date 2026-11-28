using Timeglass.Core.Domain;

namespace Timeglass.Core.Reporting;

/// <summary>Builds <see cref="TitleBreakdown"/> values: one app's time grouped by window title.</summary>
public static class TitleBreakdowns
{
    /// <summary>
    /// One app's time by title over local days <paramref name="firstDay"/> to
    /// <paramref name="lastDay"/> (both included) in <paramref name="zone"/>; see
    /// <see cref="Build(IEnumerable{ActivityInterval}, string, DateTimeOffset, DateTimeOffset, DateTimeOffset)"/>.
    /// For one day, pass the same date twice.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="lastDay"/> is before <paramref name="firstDay"/>.</exception>
    public static TitleBreakdown ForDays(
        IEnumerable<ActivityInterval> intervals,
        string appKey,
        DateOnly firstDay,
        DateOnly lastDay,
        TimeZoneInfo zone,
        DateTimeOffset now,
        TimeSpan dayStart = default)
    {
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone, dayStart);
        return Build(intervals, appKey, from, to, now);
    }

    /// <summary>
    /// Active time in the app with key <paramref name="appKey"/> inside
    /// [<paramref name="from"/>, <paramref name="to"/>), grouped by exact window title. Every
    /// title is listed, longest first (#100). Time without a recorded title (titles turned off, or
    /// a window without one) is grouped under a <see langword="null"/> title. Intervals are clipped
    /// to the range, and an open interval counts as running until <paramref name="now"/>.
    /// </summary>
    public static TitleBreakdown Build(
        IEnumerable<ActivityInterval> intervals,
        string appKey,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now) => BuildWithRetained(intervals, appKey, from, to, now, []);

    internal static TitleBreakdown BuildWithRetained(
        IEnumerable<ActivityInterval> intervals,
        string appKey,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        IEnumerable<RolledUpTotal> retained)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentException.ThrowIfNullOrEmpty(appKey);

        AppIdentity? app = null;
        var lastSeen = DateTimeOffset.MinValue;
        var total = TimeSpan.Zero;
        var untitled = TimeSpan.Zero;
        var perTitle = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (var interval in intervals)
        {
            if (interval.Kind != IntervalKind.Active || (interval.App ?? AppIdentity.Unknown).Key != appKey)
            {
                continue;
            }

            var start = Clipping.Max(interval.Start, from);
            var end = Clipping.Min(Clipping.EffectiveEnd(interval, now), to);
            if (end <= start)
            {
                continue;
            }

            if (app is null || start > lastSeen)
            {
                app = interval.App ?? AppIdentity.Unknown;
                lastSeen = start;
            }

            var length = end - start;
            total += length;
            if (string.IsNullOrEmpty(interval.Title))
            {
                untitled += length;
            }
            else
            {
                perTitle[interval.Title] = perTitle.GetValueOrDefault(interval.Title) + length;
            }
        }

        foreach (var entry in retained)
        {
            var identity = entry.App ?? AppIdentity.Unknown;
            if (entry.Kind != IntervalKind.Active || identity.Key != appKey)
            {
                continue;
            }

            if (app is null || entry.First > lastSeen)
            {
                app = identity;
                lastSeen = entry.First;
            }

            total += entry.Duration;
            untitled += entry.Duration;
        }

        var groups = perTitle
            .Select(t => (Title: (string?)t.Key, Duration: t.Value))
            .Concat(untitled > TimeSpan.Zero ? [(null, untitled)] : [])
            .OrderByDescending(t => t.Duration)
            .ThenBy(t => t.Title is null) // a named title before the untitled time on a tie
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .ToList();

        var percents = DaySummaries.RoundToHundred([.. groups.Select(t => t.Duration.Ticks)]);
        var titles = groups
            .Select((t, i) => new TitleTotal(t.Title, t.Duration, Share(t.Duration, total), percents[i]))
            .ToList();

        return new TitleBreakdown(app ?? new AppIdentity(appKey, appKey), from, to, total, titles);
    }

    private static double Share(TimeSpan part, TimeSpan total) => (double)part.Ticks / total.Ticks;
}
