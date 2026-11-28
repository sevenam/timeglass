using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using Timeglass.Core.Apps;
using Timeglass.Core.Reporting;
using Timeglass.Core.Tracking;

namespace Timeglass.Core.Settings;

/// <summary>
/// The user's settings, stored in <c>settings.json</c> (see <c>SettingsFile</c> in Storage) and
/// applied without a restart when the file changes.
/// </summary>
/// <remarks>
/// A mutable class so the configuration binder can fill it; treat a bound instance as read-only.
/// Validation uses data annotations plus <see cref="Validate"/> for the rules that need more than
/// one property. Time spans are written as <c>hh:mm:ss</c>.
/// </remarks>
public sealed class TimeglassSettings : IValidatableObject
{
    /// <summary>The port the dashboard's dev proxy expects (ADR 0003).</summary>
    public const int DefaultApiPort = 5381;

    /// <summary>
    /// After this long without keyboard or mouse input the user counts as away.
    /// <see langword="null"/> turns idle detection off.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "04:00:00",
        ErrorMessage = "{0} must be between {1} and {2}, or null to turn idle detection off.")]
    public TimeSpan? IdleThreshold { get; set; } = TrackingOptions.Default.IdleThreshold;

    /// <summary>App keys that never go idle while in front, such as video players or meeting apps.</summary>
    public List<string> IdleExemptApps { get; set; } = [];

    /// <summary>A window has to stay in front at least this long to get its own interval.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00", ErrorMessage = "{0} must be between {1} and {2}.")]
    public TimeSpan MinimumInterval { get; set; } = TrackingOptions.Default.MinimumInterval;

    /// <summary>How often the open interval is written out, bounding data loss on a crash.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00", ErrorMessage = "{0} must be between {1} and {2}.")]
    public TimeSpan CheckpointInterval { get; set; } = TrackingOptions.Default.CheckpointInterval;

    /// <summary>Whether window titles are stored. When false, no interval gets a title.</summary>
    public bool RecordWindowTitles { get; set; } = true;

    /// <summary>
    /// App keys that are not tracked at all: while one is in front, nothing is recorded.
    /// </summary>
    public List<string> ExcludedApps { get; set; } = [];

    /// <summary>App keys whose time is tracked but whose window titles are never stored.</summary>
    public List<string> AppsWithoutTitles { get; set; } = [];

    /// <summary>
    /// The user's app aliases and renames, applied on top of <see cref="AppAliasTable.BuiltIn"/>
    /// (see <see cref="AppAliasTable.WithOverrides"/>).
    /// </summary>
    public List<AppAliasSetting> AppAliases { get; set; } = [];

    /// <summary>Categories matched by ordered application-name and window-title keywords.</summary>
    public List<CategorySetting> Categories { get; set; } = [];

    /// <summary>
    /// How many months of intervals (window titles, the timeline) are kept. Older days are reduced
    /// to their totals per app (#41). <see langword="null"/>, the default, keeps everything.
    /// </summary>
    [Range(1, 120, ErrorMessage = "{0} must be between {1} and {2}, or null to keep everything.")]
    public int? RetentionMonths { get; set; }

    /// <summary>Local time work hours begin, for the workday on the main screen (#110).</summary>
    [Range(typeof(TimeSpan), "00:00:00", "23:59:00", ErrorMessage = "{0} must be between {1} and {2}.")]
    public TimeSpan WorkHoursStart { get; set; } = WorkHours.Default.Start;

    /// <summary>Local time work hours end; after <see cref="WorkHoursStart"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "23:59:00", ErrorMessage = "{0} must be between {1} and {2}.")]
    public TimeSpan WorkHoursEnd { get; set; } = WorkHours.Default.End;

    /// <summary>The work hours these settings stand for.</summary>
    public WorkHours ToWorkHours() => new(WorkHoursStart, WorkHoursEnd);

    /// <summary>Whether to exclude a short final after-hours return and its preceding absence.</summary>
    public bool DiscardAfterHoursReturns { get; set; }

    /// <summary>Minimum length of the preceding absence.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00", ErrorMessage = "{0} must be between {1} and {2}.")]
    public TimeSpan AfterHoursMinimumAway { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum length of the final return.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "04:00:00", ErrorMessage = "{0} must be between {1} and {2}.")]
    public TimeSpan AfterHoursMaximumReturn { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The current after-hours analysis options.</summary>
    public AfterHoursOptions ToAfterHoursOptions() =>
        new(DiscardAfterHoursReturns, ToWorkHours(), AfterHoursMinimumAway, AfterHoursMaximumReturn);

    /// <summary>Port of the local HTTP API (#27). Always bound to loopback.</summary>
    [Range(1024, 65535, ErrorMessage = "{0} must be between {1} and {2}.")]
    public int ApiPort { get; set; } = DefaultApiPort;

    /// <summary>
    /// Lowest level written to the log file. <c>Debug</c> adds tracking state changes (never window titles).
    /// </summary>
    [EnumDataType(typeof(LogLevel), ErrorMessage = "{0} must be one of Trace, Debug, Information, Warning, Error, Critical or None.")]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>The tracking options these settings stand for, on top of <paramref name="baseline"/>.</summary>
    public TrackingOptions ToTrackingOptions(TrackingOptions baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return baseline
            .WithIdleExemptApps(IdleExemptApps.Select(a => a.Trim()))
            .WithExcludedApps(ExcludedApps.Select(a => a.Trim()))
            .WithAppsWithoutTitles(AppsWithoutTitles.Select(a => a.Trim())) with
        {
            IdleThreshold = IdleThreshold,
            MinimumInterval = MinimumInterval,
            CheckpointInterval = CheckpointInterval,
            RecordWindowTitles = RecordWindowTitles,
        };
    }

    /// <summary><paramref name="builtIn"/> with <see cref="AppAliases"/> applied.</summary>
    public AppAliasTable ToAliasTable(AppAliasTable builtIn)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        return builtIn.WithOverrides(AppAliases.Select(a => a.ToAlias()));
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Categories is not null)
        {
            if (Categories.Any(g => g is null || string.IsNullOrWhiteSpace(g.Name)
                || g.Keywords is null || g.Keywords.Count == 0 || g.Keywords.Any(string.IsNullOrWhiteSpace)))
            {
                yield return new ValidationResult("Each category needs a name and at least one non-empty keyword.", [nameof(Categories)]);
            }

            var names = Categories.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name))
                .Select(g => g.Name.Trim()).ToList();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count
                || names.Contains("Ungrouped", StringComparer.OrdinalIgnoreCase))
            {
                yield return new ValidationResult("Category names must be unique and cannot be Ungrouped.", [nameof(Categories)]);
            }
        }

        foreach (var (name, keys) in new[]
        {
            (nameof(IdleExemptApps), IdleExemptApps),
            (nameof(ExcludedApps), ExcludedApps),
            (nameof(AppsWithoutTitles), AppsWithoutTitles),
        })
        {
            if (keys is null)
            {
                continue;
            }

            if (keys.Any(string.IsNullOrWhiteSpace))
            {
                yield return new ValidationResult($"{name} must not contain empty app keys.", [name]);
            }
        }

        if (ExcludedApps is not null && AppsWithoutTitles is not null)
        {
            var both = ExcludedApps.Select(a => a.Trim())
                .Intersect(AppsWithoutTitles.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase)
                .Where(a => a.Length > 0)
                .ToList();
            if (both.Count > 0)
            {
                yield return new ValidationResult(
                    $"{string.Join(", ", both)} cannot be in both {nameof(ExcludedApps)} and {nameof(AppsWithoutTitles)}; pick one.",
                    [nameof(ExcludedApps), nameof(AppsWithoutTitles)]);
            }
        }

        if (WorkHoursEnd <= WorkHoursStart)
        {
            yield return new ValidationResult(
                $"{nameof(WorkHoursEnd)} must be after {nameof(WorkHoursStart)}.",
                [nameof(WorkHoursEnd)]);
        }

        if (AppAliases is null)
        {
            yield break;
        }

        // Check the merged table: a rename of a built-in app may have no executables of its own.
        foreach (var error in ToAliasTable(AppAliasTable.BuiltIn).Validate())
        {
            yield return new ValidationResult($"{nameof(AppAliases)}: {error}", [nameof(AppAliases)]);
        }
    }
}

/// <summary>A named category and the keywords that match it, in priority order.</summary>
public sealed class CategorySetting
{
    /// <summary>Display name of the category.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Case-insensitive substrings matched against application names and window titles.</summary>
    public List<string> Keywords { get; set; } = [];
}

/// <summary>One entry of <see cref="TimeglassSettings.AppAliases"/>; see <see cref="AppAlias"/>.</summary>
public sealed class AppAliasSetting
{
    /// <summary>Lowercase app key. An existing key renames that app; a new one adds an app.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name. Empty keeps the existing name when renaming.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Executable file names, e.g. <c>olk.exe</c>.</summary>
    public List<string> Exes { get; set; } = [];

    /// <summary>Optional regex on the window title.</summary>
    public string? TitlePattern { get; set; }

    /// <summary>The alias this entry stands for.</summary>
    public AppAlias ToAlias() => new(Key, Name, [.. Exes ?? []], string.IsNullOrEmpty(TitlePattern) ? null : TitlePattern);
}
