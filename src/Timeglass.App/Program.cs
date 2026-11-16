using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timeglass.App.Logging;
using Timeglass.App.Tray;
using Timeglass.App.Updates;
using Timeglass.Core.Ports;
using Timeglass.Windows.Autostart;
using Velopack;

namespace Timeglass.App;

internal static partial class Program
{
    /// <summary>
    /// Entry point. Builds and starts the generic host, then runs the WinForms message loop for the
    /// tray icon on this STA thread until Quit, logoff or shutdown, and finally stops the host,
    /// which closes the open interval and writes everything to the database.
    /// </summary>
    /// <returns>0 on a normal exit, 1 if startup failed or an unhandled exception ended the app.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        // First of all: the setup program and the updater start the app with their own arguments
        // to run these hooks (#40), and expect it to exit at once. A normal start falls through.
        var restartedByUpdater = false;
        VelopackApp.Build()
            // Done below instead, after the single instance check: a second launch must open the
            // dashboard, not replace the files of the instance that is tracking.
            .SetAutoApplyOnStartup(false)
            .OnRestarted(_ => restartedByUpdater = true)
            .OnFirstRun(_ => InstalledApp.OnFirstRun(Autostart()))
            .OnBeforeUninstallFastCallback(_ => InstalledApp.OnUninstall(Autostart(), TimeglassHost.ProcessPath))
            .Run();

        // Route UI thread exceptions to Application.ThreadException (CrashHandler) instead of
        // the WinForms error dialog. Must happen before any control is created.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();

        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            instance.SignalFirst();
            return 0;
        }

        // A version downloaded by the last run is installed now, before anything is tracked.
        if (InstalledApp.ApplyPendingUpdate(new VelopackUpdater(), restartedByUpdater, args))
        {
            return 0;
        }

        IHost host;
        try
        {
            host = TimeglassHost.CreateBuilder(args).Build();
        }
#pragma warning disable CA1031 // Report any startup failure to the user instead of crashing silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowStartupError(ex);
            return 1;
        }

        try
        {
            var logger = host.Services.GetRequiredService<ILogger<TrayApplicationContext>>();
            var crashes = host.Services.GetRequiredService<CrashHandler>();
            crashes.Install();
            try
            {
                // SystemEvents attaches to the thread that first uses it. Resolve the session source
                // here so lock, sleep and shutdown events arrive through this thread's message loop.
                host.Services.GetRequiredService<ISessionEventSource>();
                host.Start();
            }
#pragma warning disable CA1031 // As above.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStartupFailed(logger, ex);
                ShowStartupError(ex);
                return 1;
            }

            using var tray = ActivatorUtilities.CreateInstance<TrayApplicationContext>(host.Services);
            instance.StartListening(tray.Activate);
            tray.Show();
            Application.Run(tray);
            return crashes.HasCrashed ? 1 : 0;
        }
        finally
        {
            TimeglassHost.StopAndDispose(host);
        }
    }

    private static AutostartRegistration Autostart() =>
        new(CurrentUserStartupRegistry.Instance, TimeglassHost.AutostartExecutable);

    private static void ShowStartupError(Exception ex) =>
        MessageBox.Show(
            $"Timeglass could not start.\n\n{ex.GetBaseException().Message}",
            "Timeglass",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Timeglass could not start")]
    private static partial void LogStartupFailed(ILogger logger, Exception exception);
}
