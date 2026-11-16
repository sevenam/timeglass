using System.Globalization;

namespace Timeglass.App.Tray;

/// <summary>Texts shown by the tray icon.</summary>
internal static class TrayText
{
    /// <summary>Windows cuts notification icon tooltips at 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Tooltip such as <c>Timeglass: 3 h 05 min active today</c>, with <c>(paused)</c> or
    /// <c>(paused until 14:30)</c> after it.
    /// </summary>
    /// <param name="activeToday">Today's active time, if known.</param>
    /// <param name="paused">Whether tracking is paused.</param>
    /// <param name="pausedUntil">Local time the pause ends, if it does.</param>
    public static string Tooltip(TimeSpan? activeToday, bool paused, DateTime? pausedUntil = null)
    {
        var text = activeToday is { } active
            ? $"Timeglass: {Duration(active)} active today"
            : "Timeglass";

        if (!paused)
        {
            return text;
        }

        return pausedUntil is { } until
            ? $"{text} (paused until {Clock(until)})"
            : text + " (paused)";
    }

    /// <summary>The menu item shown while paused: <c>Resume tracking</c>, or <c>Resume tracking (paused until 14:30)</c>.</summary>
    public static string ResumeItem(DateTime? pausedUntil) =>
        pausedUntil is { } until ? $"&Resume tracking (paused until {Clock(until)})" : "&Resume tracking";

    /// <summary><c>14:30</c>; midnight is <c>midnight</c>, since <c>00:00</c> reads as "now" to some.</summary>
    public static string Clock(DateTime local) =>
        local.TimeOfDay == TimeSpan.Zero
            ? "midnight"
            : local.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>0 min</c>, <c>45 min</c>, <c>3 h 05 min</c>. Whole minutes, rounded down.</summary>
    public static string Duration(TimeSpan duration)
    {
        var minutes = (long)Math.Max(0, Math.Floor(duration.TotalMinutes));
        return minutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:00} min");
    }

    /// <summary>
    /// The version to show, from the informational version the build stamps in: <c>0.1.0</c>, or
    /// <c>0.1.0 (3f2a1b9)</c> when it ends in the commit (<c>0.1.0+3f2a1b9c…</c>).
    /// </summary>
    public static string Version(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown version";
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return informationalVersion;
        }

        var version = informationalVersion[..plus];
        var commit = informationalVersion[(plus + 1)..];
        return commit.Length == 0
            ? version
            : $"{version} ({commit[..Math.Min(7, commit.Length)]})";
    }

    /// <summary>The menu item shown once a new version is downloaded: <c>Restart to update to 0.2.0</c>.</summary>
    public static string UpdateItem(string version) => $"Restart to &update to {version}";

    /// <summary>The <i>About</i> box: <c>Timeglass 0.1.0 (3f2a1b9)</c> and a line on what it does.</summary>
    public static string About(string? informationalVersion) =>
        $"Timeglass {Version(informationalVersion)}\n\nTracks which apps you use. Everything stays on this computer.";
}
