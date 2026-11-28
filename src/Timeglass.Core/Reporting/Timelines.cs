using Timeglass.Core.Domain;
using Timeglass.Core.Settings;

namespace Timeglass.Core.Reporting;

/// <summary>Builds <see cref="DayTimeline"/> values from stored intervals.</summary>
/// <remarks>
/// The tracker starts a new interval whenever the window title changes, so a morning in one app
/// can be hundreds of intervals. The timeline merges intervals of the same app (or away time)
/// that follow each other with a gap shorter than <see cref="DefaultMergeGap"/>, so the view
/// draws what a person would call one stretch of work.
/// </remarks>
public static class Timelines
{
    /// <summary>Gaps shorter than this between intervals of the same app are merged over.</summary>
    public static readonly TimeSpan DefaultMergeGap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The timeline of local day <paramref name="day"/> in <paramref name="zone"/>; see
    /// <see cref="Build"/>.
    /// </summary>
    /// <param name="intervals">Stored intervals, in any order.</param>
    /// <param name="day">The local date.</param>
    /// <param name="zone">The user's time zone.</param>
    /// <param name="now">The current time, so a still-open interval has an end.</param>
    /// <param name="dayStart">Local time the day begins; see <see cref="DaySummaries.Bounds"/>.</param>
    /// <param name="mergeGap">Merge gaps shorter than this; <see cref="DefaultMergeGap"/> if not given.</param>
    /// <param name="categories">When supplied, each active block includes its matching category.</param>
    public static DayTimeline ForDay(
        IEnumerable<ActivityInterval> intervals,
        DateOnly day,
        TimeZoneInfo zone,
        DateTimeOffset now,
        TimeSpan dayStart = default,
        TimeSpan? mergeGap = null,
        IReadOnlyList<CategorySetting>? categories = null)
    {
        var (from, to) = DaySummaries.Bounds(day, zone, dayStart);
        return new DayTimeline(day, from, to, Build(intervals, from, to, now, mergeGap, categories));
    }

    /// <summary>
    /// Blocks of time in [<paramref name="from"/>, <paramref name="to"/>), in time order. Intervals
    /// are clipped to the range, and an open interval counts as running until <paramref name="now"/>.
    /// Two intervals land in one block when they are the same kind and app (by key) and the gap
    /// between them is shorter than <paramref name="mergeGap"/>; intervals that touch always merge.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mergeGap"/> is negative.</exception>
    public static IReadOnlyList<TimelineBlock> Build(
        IEnumerable<ActivityInterval> intervals,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        TimeSpan? mergeGap = null,
        IReadOnlyList<CategorySetting>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var gap = mergeGap ?? DefaultMergeGap;
        ArgumentOutOfRangeException.ThrowIfLessThan(gap, TimeSpan.Zero, nameof(mergeGap));

        var clipped = intervals
            .Select(i => (Interval: i, Start: Clipping.Max(i.Start, from), End: Clipping.Min(Clipping.EffectiveEnd(i, now), to)))
            .Where(c => c.End > c.Start)
            .OrderBy(c => c.Start)
            .ThenBy(c => c.End);

        var blocks = new List<TimelineBlock>();
        foreach (var (interval, start, end) in clipped)
        {
            var app = interval.Kind == IntervalKind.Active ? interval.App ?? AppIdentity.Unknown : null;
            var category = categories is not null && interval.Kind == IntervalKind.Active
                ? Categories.Match(app, interval.Title, categories) : null;
            if (blocks.Count > 0)
            {
                var last = blocks[^1];
                var between = start - last.End;
                if (last.Kind == interval.Kind
                    && last.App?.Key == app?.Key
                    && last.Category?.Key == category?.Key
                    && (between <= TimeSpan.Zero || between < gap))
                {
                    blocks[^1] = last with
                    {
                        // A newer display name wins, as in the day summary.
                        App = app,
                        End = Clipping.Max(last.End, end),
                        Duration = last.Duration + Clipping.Max(end - Clipping.Max(start, last.End), TimeSpan.Zero),
                        IsOpen = interval.IsOpen || (last.IsOpen && last.End >= end),
                    };
                    continue;
                }
            }

            blocks.Add(new TimelineBlock(interval.Kind, app, start, end, end - start, interval.IsOpen, category));
        }

        return blocks;
    }
}
