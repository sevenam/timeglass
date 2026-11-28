using System.Text.Json.Serialization;
using Timeglass.Core.Domain;
using Timeglass.Core.Reporting;

namespace Timeglass.Api;

// The shapes the API returns. They mirror the Core reporting records, but are made for JSON and
// the generated TypeScript client: durations are whole milliseconds (the precision intervals are
// stored with), dates are ISO dates and kinds are lowercase strings. Instants keep the offset
// Core gives them: a day's bounds are in the user's zone, interval times in UTC.

/// <summary>An app as shown to the user.</summary>
/// <param name="Key">Stable key, used in URLs, e.g. <c>outlook</c>.</param>
/// <param name="Name">Display name, e.g. <c>Outlook</c>.</param>
public sealed record AppDto(string Key, string Name)
{
    internal static AppDto Create(AppIdentity app) => new(app.Key, app.DisplayName);
}

/// <summary>One app's or category's share of the time.</summary>
/// <param name="App">The app or category.</param>
/// <param name="DurationMs">Time in the app or category, in milliseconds.</param>
/// <param name="Share">Exact fraction of active time, or active plus away for categories, between 0 and 1.</param>
/// <param name="Percent">Whole percentage; the percentages of one list add up to 100.</param>
/// <param name="Color">Persistent display colour for the app.</param>
public sealed record AppTotalDto(AppDto App, long DurationMs, double Share, int Percent, string Color)
{
    internal static AppTotalDto Create(AppDayTotal total, IReadOnlyDictionary<string, string> colors) =>
        new(AppDto.Create(total.App), total.Duration.ToMs(), total.Share, total.Percent, colors[total.App.Key]);
}

/// <summary>Where the time went on one local day.</summary>
/// <param name="Date">The local date.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive); 23 or 25 hours after the start on DST days.</param>
/// <param name="ActiveMs">Total active time, in milliseconds.</param>
/// <param name="AwayMs">Total away time (idle, locked or asleep), in milliseconds.</param>
/// <param name="FirstActivity">Start of the first active time, or null if there was none.</param>
/// <param name="LastActivity">End of the last active time, or null if there was none.</param>
/// <param name="WorkdayMs">Time from the first to the last activity that falls inside the work hours in the settings (09:00 to 17:00 by default), in milliseconds.</param>
/// <param name="Apps">Active time per app, longest first.</param>
public sealed record DaySummaryDto(
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    long ActiveMs,
    long AwayMs,
    DateTimeOffset? FirstActivity,
    DateTimeOffset? LastActivity,
    long WorkdayMs,
    IReadOnlyList<AppTotalDto> Apps)
{
    internal static DaySummaryDto Create(DaySummary summary, TimeZoneInfo zone, WorkHours workHours, IReadOnlyDictionary<string, string> colors) => new(
        summary.Day,
        summary.Start,
        summary.End,
        summary.Active.ToMs(),
        summary.Away.ToMs(),
        summary.FirstActivity?.In(zone),
        summary.LastActivity?.In(zone),
        workHours.Within(summary.Day, summary.FirstActivity, summary.LastActivity, zone).ToMs(),
        [.. summary.Apps.Select(a => AppTotalDto.Create(a, colors))]);
}

/// <summary>Active or away.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityKind>))]
public enum ActivityKind
{
    /// <summary>Working in an app.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Idle, locked or asleep.</summary>
    [JsonStringEnumMemberName("away")]
    Away,
}

/// <summary>What happened when on one local day.</summary>
/// <param name="Date">The local date.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive).</param>
/// <param name="Blocks">Active and away blocks in time order. Gaps between blocks are untracked time.</param>
public sealed record TimelineDto(DateOnly Date, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<TimelineBlockDto> Blocks)
{
    internal static TimelineDto Create(DayTimeline timeline, TimeZoneInfo zone) =>
        new(timeline.Day, timeline.Start, timeline.End, [.. timeline.Blocks.Select(b => TimelineBlockDto.Create(b, zone))]);
}

/// <summary>A stretch of time in one app, or away; intervals a few seconds apart are merged.</summary>
/// <param name="Kind">Active or away.</param>
/// <param name="App">The app of an active block; null for an away block.</param>
/// <param name="Start">Start (inclusive).</param>
/// <param name="End">End (exclusive).</param>
/// <param name="DurationMs">Time tracked in the block, in milliseconds: end minus start, less the short gaps merged over.</param>
/// <param name="IsOpen">True if the block is still running.</param>
/// <param name="Category">Category for category timelines, otherwise null.</param>
public sealed record TimelineBlockDto(
    ActivityKind Kind,
    AppDto? App,
    DateTimeOffset Start,
    DateTimeOffset End,
    long DurationMs,
    bool IsOpen,
    AppDto? Category)
{
    internal static TimelineBlockDto Create(TimelineBlock block, TimeZoneInfo zone) => new(
        block.Kind.ToDto(),
        block.App is null ? null : AppDto.Create(block.App),
        block.Start.In(zone),
        block.End.In(zone),
        block.Duration.ToMs(),
        block.IsOpen,
        block.Category is null ? null : AppDto.Create(block.Category));
}

/// <summary>Where the time in one app went on one local day, by window title.</summary>
/// <param name="Date">The local date.</param>
/// <param name="App">The app. If it had no time that day, its name is its key.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive).</param>
/// <param name="TotalMs">Active time in the app, in milliseconds.</param>
/// <param name="Titles">Every title, longest first.</param>
/// <param name="Color">Persistent display colour, or null if the app has no recorded time.</param>
public sealed record AppDayDto(
    DateOnly Date,
    AppDto App,
    DateTimeOffset Start,
    DateTimeOffset End,
    long TotalMs,
    IReadOnlyList<TitleTotalDto> Titles,
    string? Color)
{
    internal static AppDayDto Create(DateOnly date, TitleBreakdown breakdown, string? color) => new(
        date,
        AppDto.Create(breakdown.App),
        breakdown.Start,
        breakdown.End,
        breakdown.Total.ToMs(),
        [.. breakdown.Titles.Select(t => new TitleTotalDto(t.Title, t.Duration.ToMs(), t.Share, t.Percent))],
        color);
}

/// <summary>Time with one window title.</summary>
/// <param name="Title">The title, or null for time where no title was recorded.</param>
/// <param name="DurationMs">Active time with this title, in milliseconds.</param>
/// <param name="Share">Exact fraction of the app's time, between 0 and 1.</param>
/// <param name="Percent">Whole percentage; the titles add up to 100.</param>
public sealed record TitleTotalDto(string? Title, long DurationMs, double Share, int Percent);

/// <summary>One application's titles assigned to a category over a range.</summary>
/// <param name="App">The application.</param>
/// <param name="TotalMs">Active time assigned to the category, in milliseconds.</param>
/// <param name="Titles">Titles and time without an available title, longest first.</param>
public sealed record CategoryAppTitlesDto(AppDto App, long TotalMs, IReadOnlyList<TitleTotalDto> Titles)
{
    internal static CategoryAppTitlesDto Create(TitleBreakdown breakdown) => new(
        AppDto.Create(breakdown.App),
        breakdown.Total.ToMs(),
        [.. breakdown.Titles.Select(t => new TitleTotalDto(t.Title, t.Duration.ToMs(), t.Share, t.Percent))]);
}

/// <summary>Totals per day and per app over a range of local days.</summary>
/// <param name="From">First local date.</param>
/// <param name="To">Last local date (included).</param>
/// <param name="Start">First instant of the first day (inclusive).</param>
/// <param name="End">First instant of the day after the last (exclusive).</param>
/// <param name="ActiveMs">Total active time, in milliseconds.</param>
/// <param name="AwayMs">Total away time, in milliseconds.</param>
/// <param name="Days">One entry per day in date order, including days without any time.</param>
/// <param name="Apps">Active time per app, or categories including away time; longest first.</param>
public sealed record RangeSummaryDto(
    DateOnly From,
    DateOnly To,
    DateTimeOffset Start,
    DateTimeOffset End,
    long ActiveMs,
    long AwayMs,
    IReadOnlyList<DayTotalDto> Days,
    IReadOnlyList<AppTotalDto> Apps)
{
    internal static RangeSummaryDto Create(RangeSummary summary, IReadOnlyDictionary<string, string> colors) => new(
        summary.FirstDay,
        summary.LastDay,
        summary.Start,
        summary.End,
        summary.Active.ToMs(),
        summary.Away.ToMs(),
        [.. summary.Days.Select(d => new DayTotalDto(d.Day, d.Start, d.End, d.Active.ToMs(), d.Away.ToMs(), [.. d.Apps.Select(a => AppTotalDto.Create(a, colors))]))],
        [.. summary.Apps.Select(a => AppTotalDto.Create(a, colors))]);
}

/// <summary>One day in a range.</summary>
/// <param name="Date">The local date.</param>
/// <param name="Start">First instant of the day (inclusive).</param>
/// <param name="End">First instant of the next day (exclusive).</param>
/// <param name="ActiveMs">Active time, in milliseconds.</param>
/// <param name="AwayMs">Away time, in milliseconds.</param>
/// <param name="Apps">Every app used that day, or categories including away time; longest first.</param>
public sealed record DayTotalDto(
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    long ActiveMs,
    long AwayMs,
    IReadOnlyList<AppTotalDto> Apps);

/// <summary>What the tracker is doing right now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackingStateKind>))]
public enum TrackingStateKind
{
    /// <summary>Recording time in <see cref="StatusDto.CurrentApp"/>.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Recording away time: the user is idle, or the PC is locked or asleep.</summary>
    [JsonStringEnumMemberName("away")]
    Away,

    /// <summary>Tracking is on but nothing is being recorded, e.g. an excluded app is in front.</summary>
    [JsonStringEnumMemberName("untracked")]
    Untracked,

    /// <summary>Tracking is paused.</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,
}

/// <summary>The tracker's current state.</summary>
/// <param name="State">What the tracker is doing.</param>
/// <param name="PausedUntil">When a pause ends by itself; null if it doesn't, or if not paused.</param>
/// <param name="CurrentApp">The app being tracked while active; null otherwise.</param>
/// <param name="Since">Start of the current active or away stretch today; null when nothing is being recorded.</param>
/// <param name="Today">Today's local date.</param>
/// <param name="Now">The server's current time, to count the running stretch from.</param>
public sealed record StatusDto(
    TrackingStateKind State,
    DateTimeOffset? PausedUntil,
    AppDto? CurrentApp,
    DateTimeOffset? Since,
    DateOnly Today,
    DateTimeOffset Now);

internal static class DtoConversions
{
    public static long ToMs(this TimeSpan duration) => duration.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// The same instant with the offset of <paramref name="zone"/>, as every time in a response has,
    /// so the dashboard can show the clock time from the string as it is.
    /// </summary>
    public static DateTimeOffset In(this DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone);

    public static ActivityKind ToDto(this IntervalKind kind) => kind == IntervalKind.Away ? ActivityKind.Away : ActivityKind.Active;
}
