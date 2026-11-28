using Timeglass.Core.Domain;

namespace Timeglass.Core.Reporting;

/// <summary>Where the time went on one local day.</summary>
/// <param name="Day">The local date the summary is for.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive). <c>End - Start</c> is 23 or 25 hours on DST days.</param>
/// <param name="Active">Total active time inside the day.</param>
/// <param name="Away">Total away time inside the day (idle, locked or asleep).</param>
/// <param name="FirstActivity">Start of the first active time in the day, or <see langword="null"/> if there was none.</param>
/// <param name="LastActivity">End of the last active time in the day, or <see langword="null"/> if there was none.</param>
/// <param name="Apps">Active time per app, longest first. Empty when there was no active time.</param>
/// <remarks>Time that is neither active nor away (paused, excluded apps, not running) is in neither total.</remarks>
public sealed record DaySummary(
    DateOnly Day,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan Active,
    TimeSpan Away,
    DateTimeOffset? FirstActivity,
    DateTimeOffset? LastActivity,
    IReadOnlyList<AppDayTotal> Apps);

/// <summary>One app's or category's share of a reporting period.</summary>
/// <param name="App">The app or category.</param>
/// <param name="Duration">Time in the app or category.</param>
/// <param name="Share">Exact fraction of active time, or active plus away for categories, between 0 and 1.</param>
/// <param name="Percent">
/// <see cref="Share"/> as a whole percentage, rounded so that the percentages of a day add up to exactly 100.
/// </param>
public sealed record AppDayTotal(AppIdentity App, TimeSpan Duration, double Share, int Percent);
