using System.Reflection;
using System.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Timeglass.App.Logging;
using Timeglass.App.Updates;
using Timeglass.Core.Tracking;
using Timeglass.Windows.Autostart;

namespace Timeglass.App.Tray;

/// <summary>
/// The tray icon and its menu. There is no main window: the WinForms message loop runs for this
/// context until <i>Quit</i>, or until the host stops.
/// </summary>
/// <remarks>
/// Create it on the UI thread (the STA thread that calls <see cref="Application.Run(ApplicationContext)"/>).
/// Events from other threads are posted to that thread.
/// </remarks>
internal sealed partial class TrayApplicationContext : ApplicationContext
{
    private readonly TrackingController _tracking;
    private readonly TodayActiveTime _today;
    private readonly DashboardLauncher _dashboard;
    private readonly LogFolder _logFolder;
    private readonly AutostartRegistration _autostart;
    private readonly UpdateService _updates;
    private readonly ILogger _logger;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenRegistration _stopping;

    private readonly Icon _activeIcon = LoadIcon("timeglass.ico");
    private readonly Icon _pausedIcon = LoadIcon("timeglass-paused.ico");
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _openDashboard = new("&Open dashboard");
    private readonly ToolStripMenuItem _pause = new("&Pause tracking");
    private readonly ToolStripMenuItem _resume = new("&Resume tracking");
    private readonly ToolStripMenuItem _startWithWindows = new("Start with &Windows");
    private readonly ToolStripMenuItem _settings = new("&Settings (coming soon)") { Enabled = false };
    private readonly ToolStripMenuItem _openLogFolder = new("Open &log folder");
    private readonly ToolStripMenuItem _about = new("&About Timeglass");
    private readonly ToolStripMenuItem _restartToUpdate = new() { Visible = false };
    private readonly ToolStripSeparator _separator = new();
    private readonly ToolStripMenuItem _quit = new("&Quit");
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _refresh = new();

    private TimeSpan? _activeToday;
    private bool _disposed;

    public TrayApplicationContext(
        TrackingController tracking,
        TodayActiveTime today,
        DashboardLauncher dashboard,
        LogFolder logFolder,
        AutostartRegistration autostart,
        UpdateService updates,
        IOptions<TimeglassOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<TrayApplicationContext> logger)
    {
        _tracking = tracking;
        _today = today;
        _dashboard = dashboard;
        _logFolder = logFolder;
        _autostart = autostart;
        _updates = updates;
        _logger = logger;

        _openDashboard.Font = new Font(_openDashboard.Font, FontStyle.Bold);
        _openDashboard.Click += (_, _) => OpenDashboard();
        _pause.DropDownItems.AddRange(
        [
            PauseItem("For &30 minutes", PauseLength.ThirtyMinutes),
            PauseItem("For 1 &hour", PauseLength.OneHour),
            PauseItem("Until &tomorrow", PauseLength.UntilTomorrow),
            PauseItem("Until I &resume", PauseLength.UntilResumed),
        ]);
        _resume.Click += OnResumeClicked;
        _startWithWindows.Click += (_, _) => ToggleAutostart();
        _openLogFolder.Click += (_, _) => OpenLogFolder();
        _about.Click += (_, _) => ShowAbout();
        _restartToUpdate.Click += (_, _) => RestartToUpdate();
        _quit.Click += (_, _) => ExitThread();
        _menu.Items.AddRange([_openDashboard, _pause, _resume, _startWithWindows, _settings, _openLogFolder, _about, _restartToUpdate, _separator, _quit]);
        _menu.Opening += (_, _) =>
        {
            RefreshAutostart();
            RefreshUpdate();
            RefreshTooltip();
        };

        // Creating the menu installed the WinForms synchronization context on this thread.
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create the tray on the UI thread.");

        _icon = new NotifyIcon { ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => OpenDashboard();

        _refresh.Interval = (int)options.Value.TooltipRefreshInterval.TotalMilliseconds;
        _refresh.Tick += (_, _) => RefreshTooltip();

        _tracking.StateChanged += OnTrackingStateChanged;
        _stopping = lifetime.ApplicationStopping.Register(() => Post(ExitThread));

        UpdateState();
        RefreshAutostart();
    }

    /// <summary>Puts the icon in the tray and starts refreshing the tooltip.</summary>
    public void Show()
    {
        _icon.Visible = true;
        _refresh.Start();
        RefreshTooltip();
    }

    /// <summary>Opens the dashboard on behalf of a second launch. Callable from any thread.</summary>
    public void Activate() => Post(OpenDashboard);

    protected override void ExitThreadCore()
    {
        // Remove the icon now; otherwise it lingers until the mouse passes over it.
        _icon.Visible = false;
        _refresh.Stop();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _tracking.StateChanged -= OnTrackingStateChanged;
            _stopping.Dispose();
            _refresh.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _separator.Dispose();
            _openDashboard.Font.Dispose();
            _activeIcon.Dispose();
            _pausedIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Icon LoadIcon(string name)
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream($"Timeglass.App.Assets.{name}")
            ?? throw new InvalidOperationException($"Missing icon resource '{name}'.");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private void Post(Action action) =>
        _ui.Post(_ =>
        {
            if (!_disposed)
            {
                action();
            }
        }, null);

    private void OpenDashboard()
    {
        if (!_dashboard.TryOpen())
        {
            _icon.ShowBalloonTip(
                5000,
                "Timeglass",
                _dashboard.Url is null
                    ? "The dashboard isn't available yet. Timeglass is tracking in the background."
                    : "Could not open the dashboard in your browser.",
                ToolTipIcon.Info);
        }
    }

    private void OpenLogFolder()
    {
        if (!_logFolder.TryOpen())
        {
            _icon.ShowBalloonTip(5000, "Timeglass", "Could not open the log folder.", ToolTipIcon.Warning);
        }
    }

    /// <summary>Shows the version, which comes from the git tag the build was made from (#38).</summary>
    private static void ShowAbout()
    {
        var version = typeof(TrayApplicationContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        MessageBox.Show(TrayText.About(version), "About Timeglass", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Shows <i>Restart to update</i> once a new version is downloaded (#40).</summary>
    private void RefreshUpdate()
    {
        var version = _updates.ReadyVersion;
        _restartToUpdate.Visible = version is not null;
        if (version is not null)
        {
            _restartToUpdate.Text = TrayText.UpdateItem(version);
        }
    }

    /// <summary>Quits the normal way, so the open interval is stored; the updater then installs and starts the new version.</summary>
    private void RestartToUpdate()
    {
        if (_updates.TryApplyAfterExit())
        {
            ExitThread();
        }
        else
        {
            _icon.ShowBalloonTip(5000, "Timeglass", "Could not start the update. It will be installed the next time Timeglass starts.", ToolTipIcon.Warning);
        }
    }

    /// <summary>Shows the registry's state, so a change made in Task Manager is not overwritten.</summary>
    private void RefreshAutostart()
    {
        try
        {
            _startWithWindows.Checked = _autostart.IsEnabled;
            _startWithWindows.Enabled = true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            LogAutostartFailed(_logger, ex);
            _startWithWindows.Checked = false;
            _startWithWindows.Enabled = false;
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            var enable = !_autostart.IsEnabled;
            _autostart.SetEnabled(enable);
            LogAutostartChanged(_logger, enable, _autostart.ExecutablePath);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            LogAutostartFailed(_logger, ex);
            _icon.ShowBalloonTip(5000, "Timeglass", "Could not change whether Timeglass starts with Windows.", ToolTipIcon.Warning);
        }

        RefreshAutostart();
    }

    private ToolStripMenuItem PauseItem(string text, PauseLength length)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => SetPaused(() => _tracking.PauseAsync(length));
        return item;
    }

    private void OnResumeClicked(object? sender, EventArgs e) => SetPaused(_tracking.ResumeAsync);

    private async void SetPaused(Func<Task> change)
    {
        _pause.Enabled = false;
        _resume.Enabled = false;
        try
        {
            await change().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // An async void handler must not throw into the message loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPauseFailed(_logger, ex);
        }
        finally
        {
            if (!_disposed)
            {
                _pause.Enabled = true;
                _resume.Enabled = true;
            }
        }
    }

    private void OnTrackingStateChanged() => Post(() =>
    {
        UpdateState();
        RefreshTooltip();
    });

    private void UpdateState()
    {
        var paused = _tracking.IsPaused;
        _pause.Visible = !paused;
        _resume.Visible = paused;
        _resume.Text = TrayText.ResumeItem(PausedUntilLocal());
        _icon.Icon = paused ? _pausedIcon : _activeIcon;
        _icon.Text = Tooltip();
    }

    private async void RefreshTooltip()
    {
        try
        {
            _activeToday = await _today.GetAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The tooltip is not worth crashing over; keep the last value.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRefreshFailed(_logger, ex);
        }

        if (!_disposed)
        {
            _icon.Text = Tooltip();
        }
    }

    private string Tooltip() => TrayText.Tooltip(_activeToday, _tracking.IsPaused, PausedUntilLocal());

    private DateTime? PausedUntilLocal() => _tracking.PausedUntil?.ToLocalTime().DateTime;

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not pause or resume tracking")]
    private static partial void LogPauseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start with Windows changed (enabled: {Enabled}) for {Path}")]
    private static partial void LogAutostartChanged(ILogger logger, bool enabled, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read or change the autostart registration")]
    private static partial void LogAutostartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read today's active time")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
