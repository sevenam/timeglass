using Timeglass.Core.Domain;
using Timeglass.Core.Settings;

namespace Timeglass.Core.Reporting;

/// <summary>Assigns recorded time to categories without changing the stored intervals.</summary>
public static class Categories
{
    /// <summary>Builds a range summary by category, matching application names and window titles.</summary>
    public static RangeSummary Build(
        IEnumerable<ActivityInterval> intervals, IEnumerable<RolledUpTotal> rolledUp,
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(rolledUp);
        ArgumentNullException.ThrowIfNull(categories);

        var summary = RangeSummaries.Build(
            intervals.Select(i => i.Kind == IntervalKind.Active ? i with { App = Match(i.App, i.Title, categories) } : i),
            firstDay, lastDay, zone, now,
            rolledUp: rolledUp.Select(t => t.Kind == IntervalKind.Active ? t with { App = Match(t.App, null, categories) } : t));
        var awayName = "Away";
        if (categories.Any(g => string.Equals(g.Name, awayName, StringComparison.OrdinalIgnoreCase)))
        {
            awayName = "Away (tracked)";
            for (var suffix = 2; categories.Any(g => string.Equals(g.Name, awayName, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                awayName = $"Away (tracked {suffix})";
            }
        }

        var awayCategory = new AppIdentity("away", awayName);
        return summary with
        {
            Apps = IncludeAway(summary.Apps, summary.Away, awayCategory),
            Days = [.. summary.Days.Select(d => d with { Apps = IncludeAway(d.Apps, d.Away, awayCategory) })],
        };
    }

    private static IReadOnlyList<AppDayTotal> IncludeAway(IReadOnlyList<AppDayTotal> apps, TimeSpan away, AppIdentity awayCategory)
    {
        if (away <= TimeSpan.Zero)
        {
            return apps;
        }

        var totals = apps.Select(a => (a.App, a.Duration))
            .Append((App: awayCategory, Duration: away))
            .OrderByDescending(a => a.Duration)
            .ThenBy(a => a.App.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.App.Key, StringComparer.Ordinal)
            .ToList();
        var sum = totals.Aggregate(TimeSpan.Zero, (total, a) => total + a.Duration);
        var percents = DaySummaries.RoundToHundred([.. totals.Select(a => a.Duration.Ticks)]);
        return [.. totals.Select((a, i) => new AppDayTotal(a.App, a.Duration, (double)a.Duration.Ticks / sum.Ticks, percents[i]))];
    }

    /// <summary>Summarises the applications contributing to one category over a range.</summary>
    public static RangeSummary BuildForCategory(
        IEnumerable<ActivityInterval> intervals, IEnumerable<RolledUpTotal> rolledUp,
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories, string categoryName)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(rolledUp);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return RangeSummaries.Build(
            intervals.Where(i => i.Kind == IntervalKind.Active
                && string.Equals(Match(i.App, i.Title, categories).DisplayName, categoryName, StringComparison.OrdinalIgnoreCase)),
            firstDay, lastDay, zone, now,
            rolledUp: rolledUp.Where(t => t.Kind == IntervalKind.Active
                && string.Equals(Match(t.App, null, categories).DisplayName, categoryName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Summarises one application's titles assigned to a category, including retained untitled time.</summary>
    public static TitleBreakdown BuildCategoryAppTitles(
        IEnumerable<ActivityInterval> intervals, IEnumerable<RolledUpTotal> rolledUp,
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone, DateTimeOffset now,
        IReadOnlyList<CategorySetting> categories, string categoryName, string appKey)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(rolledUp);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        var (from, to) = RangeSummaries.Bounds(firstDay, lastDay, zone);
        var filtered = intervals.Where(i => i.Kind == IntervalKind.Active
            && string.Equals(Match(i.App, i.Title, categories).DisplayName, categoryName, StringComparison.OrdinalIgnoreCase));
        var retained = rolledUp.Where(t => t.Day >= firstDay && t.Day <= lastDay
            && t.Kind == IntervalKind.Active
            && string.Equals(Match(t.App, null, categories).DisplayName, categoryName, StringComparison.OrdinalIgnoreCase));
        return TitleBreakdowns.BuildWithRetained(filtered, appKey, from, to, now, retained);
    }

    /// <summary>Returns the first matching category for an application and title.</summary>
    public static AppIdentity Match(AppIdentity? app, string? title, IReadOnlyList<CategorySetting> categories)
    {
        var name = (app ?? AppIdentity.Unknown).DisplayName;
        for (var i = 0; i < categories.Count; i++)
        {
            if (categories[i].Keywords.Any(keyword =>
                name.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)
                || (title?.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                return new AppIdentity($"category-{i}", categories[i].Name);
            }
        }

        return new AppIdentity("ungrouped", "Ungrouped");
    }
}
