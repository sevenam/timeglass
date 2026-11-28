namespace Timeglass.Core.Reporting;

/// <summary>Totals per day and per app over a range of local days, such as a week.</summary>
/// <param name="FirstDay">First local date in the range.</param>
/// <param name="LastDay">Last local date in the range (included).</param>
/// <param name="Start">First instant of <paramref name="FirstDay"/> (inclusive).</param>
/// <param name="End">First instant of the day after <paramref name="LastDay"/> (exclusive).</param>
/// <param name="Active">Total active time in the range.</param>
/// <param name="Away">Total away time in the range.</param>
/// <param name="Days">One entry per day, in date order, including days without any time.</param>
/// <param name="Apps">
/// Active time per app over the whole range, longest first, with shares of <paramref name="Active"/>.
/// In a keyword-category summary, the entries also include away time and shares are of active plus away.
/// </param>
public sealed record RangeSummary(
    DateOnly FirstDay,
    DateOnly LastDay,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Active,
    TimeSpan Away,
    IReadOnlyList<DayTotal> Days,
    IReadOnlyList<AppDayTotal> Apps);

/// <summary>One day's totals in a <see cref="RangeSummary"/>.</summary>
/// <param name="Day">The local date.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive).</param>
/// <param name="Active">Active time in the day.</param>
/// <param name="Away">Away time in the day.</param>
/// <param name="Apps">Active time per app that day, or categories including away time in a keyword-category summary.</param>
public sealed record DayTotal(
    DateOnly Day,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Active,
    TimeSpan Away,
    IReadOnlyList<AppDayTotal> Apps);
