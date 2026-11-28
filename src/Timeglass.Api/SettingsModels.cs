using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timeglass.Core.Settings;

namespace Timeglass.Api;

// The rest of settings.json (the API port, the log level, timing details) is kept as it is when
// these are saved. The summary stays on one line: it becomes the OpenAPI description, and a line
// break in it comes out differently on Windows.

/// <summary>The settings the dashboard can change; the rest of <c>settings.json</c> is kept as it is.</summary>
/// <param name="IdleThresholdSeconds">Seconds without keyboard or mouse input before the user counts as away, 30 to 14400; null turns idle detection off.</param>
/// <param name="IdleExemptApps">App keys that never go idle while in front, such as video players.</param>
/// <param name="ExcludedApps">App keys that are not tracked at all.</param>
/// <param name="RecordWindowTitles">Whether window titles are stored.</param>
/// <param name="AppsWithoutTitles">App keys whose time is tracked but whose window titles are never stored.</param>
/// <param name="AppAliases">The user's app renames and aliases, applied on top of the built-in ones.</param>
/// <param name="StartWithWindows">Whether the app starts when the user signs in; null where the host can't tell (read) or to leave it as it is (write).</param>
/// <param name="WorkHoursStartMinutes">Minutes after local midnight that work hours begin, 0 to 1439; null keeps the current value (write).</param>
/// <param name="WorkHoursEndMinutes">Minutes after local midnight that work hours end, after the start and at most 1439; null keeps the current value (write).</param>
/// <param name="Categories">Ordered categories of keywords, or null to leave them unchanged.</param>
/// <param name="DiscardAfterHoursReturns">Whether to exclude qualifying after-hours returns; null keeps the current value (write).</param>
/// <param name="AfterHoursMinimumAwayMinutes">Minimum preceding away time in minutes; null keeps the current value (write).</param>
/// <param name="AfterHoursMaximumReturnMinutes">Maximum final return time in minutes; null keeps the current value (write).</param>
public sealed record SettingsDto(
    int? IdleThresholdSeconds,
    IReadOnlyList<string> IdleExemptApps,
    IReadOnlyList<string> ExcludedApps,
    bool RecordWindowTitles,
    IReadOnlyList<string> AppsWithoutTitles,
    IReadOnlyList<AppAliasDto> AppAliases,
    bool? StartWithWindows,
    int? WorkHoursStartMinutes = null,
    int? WorkHoursEndMinutes = null,
    IReadOnlyList<CategoryDto>? Categories = null,
    bool? DiscardAfterHoursReturns = null,
    int? AfterHoursMinimumAwayMinutes = null,
    int? AfterHoursMaximumReturnMinutes = null)
{
    internal static SettingsDto Create(TimeglassSettings settings, bool? startWithWindows) => new(
        settings.IdleThreshold is { } idle ? (int)Math.Round(idle.TotalSeconds) : null,
        [.. settings.IdleExemptApps ?? []],
        [.. settings.ExcludedApps ?? []],
        settings.RecordWindowTitles,
        [.. settings.AppsWithoutTitles ?? []],
        [.. (settings.AppAliases ?? []).Select(AppAliasDto.Create)],
        startWithWindows,
        (int)settings.WorkHoursStart.TotalMinutes,
        (int)settings.WorkHoursEnd.TotalMinutes,
        [.. (settings.Categories ?? []).Select(g => new CategoryDto(g.Name, g.Keywords))],
        settings.DiscardAfterHoursReturns,
        (int)settings.AfterHoursMinimumAway.TotalMinutes,
        (int)settings.AfterHoursMaximumReturn.TotalMinutes);

    /// <summary>
    /// These settings on top of <paramref name="current"/>: the fields the dashboard doesn't edit
    /// keep their current values. App keys and executables are trimmed and duplicates dropped;
    /// empty ones are kept so validation can name them.
    /// </summary>
    internal TimeglassSettings ToSettings(TimeglassSettings current) => new()
    {
        IdleThreshold = IdleThresholdSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
        IdleExemptApps = Keys(IdleExemptApps),
        ExcludedApps = Keys(ExcludedApps),
        RecordWindowTitles = RecordWindowTitles,
        AppsWithoutTitles = Keys(AppsWithoutTitles),
        AppAliases = [.. (AppAliases ?? []).Select(a => a.ToSetting())],
        Categories = Categories is null ? current.Categories : [.. Categories.Select(g => g?.ToSetting() ?? new CategorySetting())],
        MinimumInterval = current.MinimumInterval,
        CheckpointInterval = current.CheckpointInterval,
        RetentionMonths = current.RetentionMonths,
        ApiPort = current.ApiPort,
        LogLevel = current.LogLevel,
        WorkHoursStart = WorkHoursStartMinutes is { } start ? TimeSpan.FromMinutes(start) : current.WorkHoursStart,
        WorkHoursEnd = WorkHoursEndMinutes is { } end ? TimeSpan.FromMinutes(end) : current.WorkHoursEnd,
        DiscardAfterHoursReturns = DiscardAfterHoursReturns ?? current.DiscardAfterHoursReturns,
        AfterHoursMinimumAway = AfterHoursMinimumAwayMinutes is { } away ? TimeSpan.FromMinutes(away) : current.AfterHoursMinimumAway,
        AfterHoursMaximumReturn = AfterHoursMaximumReturnMinutes is { } returned ? TimeSpan.FromMinutes(returned) : current.AfterHoursMaximumReturn,
    };

    internal static List<string> Keys(IEnumerable<string?>? keys) =>
        [.. (keys ?? []).Select(k => k?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>A named category of keywords, matched in the order categories are listed.</summary>
/// <param name="Name">Category name.</param>
/// <param name="Keywords">Case-insensitive substrings to match.</param>
public sealed record CategoryDto(string Name, IReadOnlyList<string> Keywords)
{
    internal CategorySetting ToSetting() => new()
    {
        Name = Name?.Trim() ?? string.Empty,
        Keywords = SettingsDto.Keys(Keywords),
    };
}

/// <summary>An app rename or alias: which executables (and window titles) make up an app, and its name.</summary>
/// <param name="Key">Lowercase app key. An existing key renames that app; a new one adds an app.</param>
/// <param name="Name">Display name. Empty keeps the existing name when renaming.</param>
/// <param name="Exes">Executable file names, e.g. olk.exe.</param>
/// <param name="TitlePattern">Optional regular expression the window title must match; null for any title.</param>
public sealed record AppAliasDto(string Key, string Name, IReadOnlyList<string> Exes, string? TitlePattern)
{
    internal static AppAliasDto Create(AppAliasSetting alias) =>
        new(alias.Key, alias.Name, [.. alias.Exes ?? []], string.IsNullOrEmpty(alias.TitlePattern) ? null : alias.TitlePattern);

    internal AppAliasSetting ToSetting() => new()
    {
        Key = Key?.Trim() ?? string.Empty,
        Name = Name?.Trim() ?? string.Empty,
        Exes = SettingsDto.Keys(Exes),
        TitlePattern = string.IsNullOrWhiteSpace(TitlePattern) ? null : TitlePattern,
    };
}

/// <summary>How long to pause tracking for; the same choices as the tray menu.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PauseDuration>))]
public enum PauseDuration
{
    /// <summary>Until resumed.</summary>
    [JsonStringEnumMemberName("untilResumed")]
    UntilResumed,

    /// <summary>30 minutes.</summary>
    [JsonStringEnumMemberName("thirtyMinutes")]
    ThirtyMinutes,

    /// <summary>One hour.</summary>
    [JsonStringEnumMemberName("oneHour")]
    OneHour,

    /// <summary>Until local midnight.</summary>
    [JsonStringEnumMemberName("untilTomorrow")]
    UntilTomorrow,
}

/// <summary>A request to pause tracking.</summary>
/// <param name="Duration">How long to pause for.</param>
public sealed record PauseRequest(PauseDuration Duration);

/// <summary>Confirms a deletion.</summary>
/// <param name="Confirm">The range being deleted, written as <c>from..to</c> (e.g. <c>2026-11-02..2026-11-04</c>), so a request can't delete a range it didn't mean to.</param>
public sealed record DeleteIntervalsRequest(string? Confirm);

/// <summary>
/// Validation of settings sent to the API, with the rules of the settings model itself (#22): its
/// data annotations and <see cref="TimeglassSettings.Validate"/>, as when <c>settings.json</c> is loaded.
/// </summary>
internal static class SettingsValidation
{
    /// <summary>The errors by <see cref="SettingsDto"/> field name; empty if the settings are valid.</summary>
    public static Dictionary<string, string[]> Validate(TimeglassSettings settings)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [string.Empty]).Select(m => (Field: FieldName(m), Message: r.ErrorMessage ?? "Invalid value.")))
            .GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private static string FieldName(string member) => member switch
    {
        nameof(TimeglassSettings.IdleThreshold) => "idleThresholdSeconds",
        nameof(TimeglassSettings.WorkHoursStart) => "workHoursStartMinutes",
        nameof(TimeglassSettings.WorkHoursEnd) => "workHoursEndMinutes",
        nameof(TimeglassSettings.AfterHoursMinimumAway) => "afterHoursMinimumAwayMinutes",
        nameof(TimeglassSettings.AfterHoursMaximumReturn) => "afterHoursMaximumReturnMinutes",
        _ => JsonNamingPolicy.CamelCase.ConvertName(member),
    };
}
