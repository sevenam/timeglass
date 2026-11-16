using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Timeglass.App.Updates;

/// <summary>
/// Keeps an installed copy up to date (#40): looks for a new version shortly after the app starts
/// and once a day after that, and downloads it in the background.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is installed while the app runs. A downloaded version waits (<see cref="ReadyVersion"/>)
/// until the next start, which installs it before tracking begins
/// (<see cref="InstalledApp.ApplyPendingUpdate"/>), or until the user picks <i>Restart to update</i>
/// in the tray menu.
/// </para>
/// <para>
/// The day is measured on the wall clock, looked at every <see cref="PollInterval"/>, so a check
/// that came due while the PC slept runs soon after it wakes. A failed check (offline, GitHub
/// down) is logged and tried again at the next look.
/// </para>
/// </remarks>
internal sealed partial class UpdateService : BackgroundService
{
    /// <summary>How long after start the first check runs, so it doesn't compete with startup.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>Time between two successful checks.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>How often the service looks at the clock, and how soon a failed check is retried.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly IAppUpdater _updater;
    private readonly IOptions<TimeglassOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private string? _readyVersion;

    public UpdateService(
        IAppUpdater updater,
        IOptions<TimeglassOptions> options,
        TimeProvider time,
        ILogger<UpdateService> logger)
    {
        _updater = updater;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The downloaded version that the next start installs; <see langword="null"/> if there is none.</summary>
    public string? ReadyVersion => Volatile.Read(ref _readyVersion);

    /// <summary>
    /// Starts the updater for the <see cref="ReadyVersion"/>: it installs once this process has
    /// exited and starts the app again. The caller quits the app when this returns <see langword="true"/>.
    /// </summary>
    public bool TryApplyAfterExit()
    {
        if (ReadyVersion is not { } version)
        {
            return false;
        }

        try
        {
            _updater.ApplyAfterExit([]);
            LogApplying(_logger, version);
            return true;
        }
        // A failed update must not take the tracker down; the next start tries again.
        catch (Exception ex) when (UpdateFailures.IsRecoverable(ex))
        {
            LogApplyFailed(_logger, ex);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_options.Value.CheckForUpdates)
            {
                LogDisabled(_logger);
                return;
            }

            if (!IsInstalled())
            {
                return;
            }

            await Task.Delay(StartupDelay, _time, stoppingToken).ConfigureAwait(false);

            DateTimeOffset? lastCheck = null;
            while (true)
            {
                var now = _time.GetUtcNow();
                if ((lastCheck is not { } last || now - last >= CheckInterval)
                    && await CheckAsync(stoppingToken).ConfigureAwait(false))
                {
                    lastCheck = now;
                }

                await Task.Delay(PollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    private bool IsInstalled()
    {
        try
        {
            if (_updater.IsInstalled)
            {
                // Downloaded by an earlier run that could not install it.
                Volatile.Write(ref _readyVersion, _updater.PendingVersion);
                return true;
            }

            LogNotInstalled(_logger);
            return false;
        }
        // Without updates the app still does its job.
        catch (Exception ex) when (UpdateFailures.IsRecoverable(ex))
        {
            LogCheckFailed(_logger, ex);
            return false;
        }
    }

    /// <returns>Whether the feed was reached; <see langword="false"/> means try again soon.</returns>
    private async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await _updater.DownloadLatestAsync(cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                LogUpToDate(_logger);
            }
            else if (version != ReadyVersion)
            {
                Volatile.Write(ref _readyVersion, version);
                LogDownloaded(_logger, version);
            }

            return true;
        }
        // Offline, rate limited, disk full: whatever it is, try again later. Only the host
        // stopping is passed on.
        catch (Exception ex) when (UpdateFailures.IsRecoverable(ex)
            && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            LogCheckFailed(_logger, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update checks are turned off (CheckForUpdates)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not installed by the setup program, so this copy does not update itself")]
    private static partial void LogNotInstalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No newer version")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded version {Version}; the next start installs it")]
    private static partial void LogDownloaded(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restarting to install version {Version}")]
    private static partial void LogApplying(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check for or download an update")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not start the update")]
    private static partial void LogApplyFailed(ILogger logger, Exception exception);
}
