using Timeglass.App.Logging;
using Timeglass.Storage;
using Timeglass.Storage.Settings;

namespace Timeglass.App;

/// <summary>
/// Host settings, bound from the <c>Timeglass</c> configuration section (command line such as
/// <c>--Timeglass:DatabasePath=D:\test.db</c>, <c>DOTNET_Timeglass__DatabasePath</c>, or an
/// optional <c>appsettings.json</c> next to the exe). These are for development and tests; the
/// user's settings are in <see cref="Core.Settings.TimeglassSettings"/>.
/// </summary>
internal sealed class TimeglassOptions
{
    public const string Section = "Timeglass";

    /// <summary>Database file. Defaults to <see cref="TimeglassDatabase.DefaultPath"/>.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Where <i>Open dashboard</i> goes instead of the app's own API (for example a Vite dev
    /// server). Empty by default: the menu signs the browser in to the embedded API (#29).
    /// </summary>
    public Uri? DashboardUrl { get; set; }

    /// <summary>
    /// The file with the API's port and token for other tools. Defaults to
    /// <see cref="Api.ApiRuntimeFile.DefaultPath"/>.
    /// </summary>
    public string? ApiRuntimeFilePath { get; set; }

    /// <summary>How often the tray tooltip refreshes today's active time.</summary>
    public TimeSpan TooltipRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The user's settings file. Defaults to <see cref="SettingsFile.DefaultPath"/>.</summary>
    public string? SettingsPath { get; set; }

    /// <summary>
    /// Whether an installed copy looks for new versions on GitHub and downloads them (#40). On by
    /// default; <c>DOTNET_Timeglass__CheckForUpdates=false</c> turns the app's only network use off.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Folder for the log files. Defaults to <see cref="LogFolder.DefaultPath"/>.</summary>
    public string? LogDirectory { get; set; }
}
