using Timeglass.Core.Domain;

namespace Timeglass.Core.Reporting;

/// <summary>What happened when on one local day, as blocks in time order.</summary>
/// <param name="Day">The local date the timeline is for.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive).</param>
/// <param name="Blocks">Active and away blocks, ordered by start, clipped to the day. Gaps between blocks are untracked time.</param>
public sealed record DayTimeline(
    DateOnly Day,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<TimelineBlock> Blocks);

/// <summary>
/// A stretch of time in one app (or away), made of one or more stored intervals that follow each
/// other with at most a short gap.
/// </summary>
/// <param name="Kind">Active or away.</param>
/// <param name="App">The app for an active block; <see langword="null"/> for an away block.</param>
/// <param name="Start">Start of the first interval in the block (inclusive).</param>
/// <param name="End">End of the last interval in the block (exclusive).</param>
/// <param name="Duration">
/// Time actually tracked in the block: <c>End - Start</c> less the short gaps that were merged over.
/// </param>
/// <param name="IsOpen">True if the block ends with the interval that is still running.</param>
/// <param name="Category">The matching category, otherwise null.</param>
public sealed record TimelineBlock(
    IntervalKind Kind,
    AppIdentity? App,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Duration,
    bool IsOpen,
    AppIdentity? Category = null);
