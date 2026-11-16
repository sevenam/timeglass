using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Timeglass.App.Logging;
using Timeglass.App.Settings;
using Timeglass.App.Updates;
using Timeglass.Core.Apps;
using Timeglass.Core.Ports;
using Timeglass.Core.Settings;
using Timeglass.Core.Tracking;
using Timeglass.Storage;
using Timeglass.Storage.Settings;
using Timeglass.Windows.Autostart;
using Timeglass.Windows.Foreground;
using Timeglass.Windows.Idle;
using Timeglass.Windows.Sessions;

namespace Timeglass.App;

/// <summary>The composition root: the only place that knows every project and wires them up.</summary>
internal static partial class TimeglassHost
{
    /// <summary>
    /// Creates the generic host builder with configuration, logging and every service registered.
    /// The container validates the whole graph when it is built, so a missing registration fails
    /// at startup (and in the composition test) instead of on first use.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "Timeglass",
            // Started from autostart or a shortcut, the working directory can be anything.
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        // There is no console; the tray menu and session events end the app.
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        builder.Logging.AddTimeglassLogging();

        AddTimeglass(builder.Services);
        return builder;
    }

    /// <summary>Registers storage, the engine, the Windows sources and the host services.</summary>
    public static IServiceCollection AddTimeglass(IServiceCollection services)
    {
        services.AddOptions<TimeglassOptions>().BindConfiguration(TimeglassOptions.Section);
        services.AddSingleton(TimeProvider.System);

        // The user's settings.json, watched for changes.
        services.AddSingleton(sp => SettingsConfiguration.Open(
            sp.GetRequiredService<IOptions<TimeglassOptions>>().Value.SettingsPath ?? SettingsFile.DefaultPath));
        AddTimeglassSettings(services);

        // Storage. Opening the database applies migrations and closes intervals left open by a crash.
        services.AddSingleton(OpenDatabase);
        services.AddSingleton<IntervalRepository>();
        services.AddSingleton<IIntervalSink>(sp => sp.GetRequiredService<IntervalRepository>());

        // Engine, configured from the settings. The service reads the current options before
        // every event; the resolver gets a new alias table whenever the settings change.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<LiveSettings>();
            var resolver = new AliasAppIdentityResolver(settings.Current.Aliases);
            settings.Changed += s => resolver.Table = s.Aliases;
            return resolver;
        });
        services.AddSingleton<IAppIdentityResolver>(sp => sp.GetRequiredService<AliasAppIdentityResolver>());
        services.AddSingleton(sp => new TrackingEngine(
            sp.GetRequiredService<IAppIdentityResolver>(),
            sp.GetRequiredService<LiveSettings>().GetTrackingOptions()));
        services.AddSingleton(sp => new TrackingService(
            sp.GetRequiredService<TrackingEngine>(),
            sp.GetRequiredService<IForegroundWindowSource>(),
            sp.GetRequiredService<IIdleSource>(),
            sp.GetRequiredService<ISessionEventSource>(),
            sp.GetRequiredService<IIntervalSink>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TrackingService>>())
        {
            CurrentOptions = sp.GetRequiredService<LiveSettings>().GetTrackingOptions,
        });

        // Windows sources.
        services.AddSingleton(new ForegroundWatcherOptions());
        services.AddSingleton<Win32ForegroundWindowSource>();
        services.AddSingleton<IForegroundWindowSource>(sp => sp.GetRequiredService<Win32ForegroundWindowSource>());
        services.AddSingleton<IIdleSource, Win32IdleSource>();
        services.AddSingleton<Win32SessionEventSource>();
        services.AddSingleton<ISessionEventSource>(sp => sp.GetRequiredService<Win32SessionEventSource>());

        // Host services, started in this order and stopped in reverse: the API stops before tracking.
        services.AddSingleton<TrackingController>();
        services.AddHostedService(sp => sp.GetRequiredService<TrackingController>());
        services.AddSingleton<ApiServer>();
        services.AddHostedService(sp => sp.GetRequiredService<ApiServer>());
        services.AddSingleton<TodayActiveTime>();
        services.AddSingleton<DashboardLauncher>();
        services.AddSingleton<LogFolder>();
        services.AddSingleton(_ => new AutostartRegistration(CurrentUserStartupRegistry.Instance, AutostartExecutable));

        // What the dashboard changes through the API (#36).
        services.AddSingleton<FileSettingsStore>();
        services.AddSingleton<RegistryAutostart>();
        services.AddSingleton<TrackerDataDeletion>();
        services.AddSingleton<CrashHandler>();

        // Updates for an installed copy (#40). Last, so the first check starts after everything else.
        services.AddSingleton<IAppUpdater, VelopackUpdater>();
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

        return services;
    }

    /// <summary>
    /// Binds <see cref="TimeglassSettings"/> to the registered <see cref="SettingsConfiguration"/>,
    /// validated with data annotations when the host starts and on every reload, and registers
    /// <see cref="LiveSettings"/>, which is what the app reads.
    /// </summary>
    public static IServiceCollection AddTimeglassSettings(IServiceCollection services)
    {
        services.AddOptions<TimeglassSettings>()
            .Configure<SettingsConfiguration>((settings, source) => source.Configuration.Bind(settings))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Makes IOptionsMonitor<TimeglassSettings> and LiveSettings see edits to the file.
        services.AddSingleton<IOptionsChangeTokenSource<TimeglassSettings>>(sp =>
            new ConfigurationChangeTokenSource<TimeglassSettings>(sp.GetRequiredService<SettingsConfiguration>().Configuration));

        services.AddSingleton(sp => new LiveSettings(
            sp.GetRequiredService<IOptionsFactory<TimeglassSettings>>(),
            sp.GetRequiredService<IEnumerable<IOptionsChangeTokenSource<TimeglassSettings>>>(),
            () => sp.GetRequiredService<ILogger<LiveSettings>>()));

        return services;
    }

    /// <summary>
    /// The exe that <i>Start with Windows</i> registers: the one running now, or for a copy
    /// installed by the setup program (#40) its launcher, whose path stays the same across updates.
    /// </summary>
    public static string AutostartExecutable => InstalledApp.StableLauncher(ProcessPath);

    /// <summary>Full path of the running exe.</summary>
    public static string ProcessPath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Timeglass.exe");

    /// <summary>
    /// Stops and disposes the host off the UI thread. Called after the message loop has ended,
    /// when nothing pumps the WinForms synchronization context any more.
    /// </summary>
    public static void StopAndDispose(IHost host) =>
        Task.Run(async () =>
        {
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    // IntervalRepository is only IAsyncDisposable: it drains its queue to the database.
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    host.Dispose();
                }
            }
        }).GetAwaiter().GetResult();

    private static TimeglassDatabase OpenDatabase(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<TimeglassOptions>>().Value;
        var logger = services.GetRequiredService<ILogger<TimeglassDatabase>>();

        var database = TimeglassDatabase.Open(options.DatabasePath ?? TimeglassDatabase.DefaultPath);
        LogDatabaseOpened(logger, database.Path, database.RecoveredIntervals);
        return database;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Opened {Path}; closed {Recovered} interval(s) left open by a previous run")]
    private static partial void LogDatabaseOpened(ILogger logger, string path, int recovered);
}
