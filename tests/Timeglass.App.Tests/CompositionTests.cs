using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Timeglass.Api;
using Timeglass.App.Tray;
using Timeglass.App.Updates;
using Timeglass.Core.Domain;
using Timeglass.Core.Ports;
using Timeglass.Storage;

namespace Timeglass.App.Tests;

public sealed class CompositionTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private HostApplicationBuilder CreateBuilder()
    {
        var builder = TimeglassHost.CreateBuilder([]);
        builder.Configuration[$"{TimeglassOptions.Section}:{nameof(TimeglassOptions.DatabasePath)}"] = _folder.DatabasePath;
        builder.Configuration[$"{TimeglassOptions.Section}:{nameof(TimeglassOptions.SettingsPath)}"] = _folder.SettingsPath;
        builder.Configuration[$"{TimeglassOptions.Section}:{nameof(TimeglassOptions.ApiRuntimeFilePath)}"] = _folder.ApiRuntimeFilePath;
        return builder;
    }

    [Fact]
    public void Container_resolves_the_full_graph()
    {
        // ValidateOnBuild checks that every registration can be constructed, without constructing
        // anything (so no hooks or database yet).
        var builder = CreateBuilder();
        using var host = builder.Build();

        // The tray is created on the UI thread, outside the container; its dependencies must be in it.
        var isService = host.Services.GetRequiredService<IServiceProviderIsService>();
        var trayDependencies = typeof(TrayApplicationContext).GetConstructors().Single().GetParameters();
        trayDependencies.ShouldAllBe(p => isService.IsService(p.ParameterType));

        builder.Services.ShouldContain(d => d.ServiceType == typeof(IHostedService));
        File.Exists(_folder.DatabasePath).ShouldBeFalse();
    }

    [Fact(Timeout = Desktop.TestTimeout)]
    [Trait("Category", Desktop.Category)]
    public async Task Host_starts_tracking_and_stores_the_interval_on_stop()
    {
        await Desktop.RunOnStaThreadAsync(() =>
        {
            var host = CreateBuilder().Build();
            try
            {
                host.Services.GetRequiredService<ISessionEventSource>();
                host.Start();
                host.Services.GetRequiredService<TrackingController>().IsPaused.ShouldBeFalse();
                CheckTheApiIsUp(host.Services.GetRequiredService<ApiServer>());

                // The test host was not installed by the setup program: no update checks, no network.
                host.Services.GetRequiredService<IAppUpdater>().ShouldBeOfType<VelopackUpdater>().IsInstalled.ShouldBeFalse();
                host.Services.GetServices<IHostedService>().ShouldContain(host.Services.GetRequiredService<UpdateService>());

                using var tray = ActivatorUtilities.CreateInstance<TrayApplicationContext>(host.Services);
            }
            finally
            {
                TimeglassHost.StopAndDispose(host);
            }
        });

        var intervals = await ReadIntervalsAsync();
        intervals.ShouldNotBeEmpty();
        intervals.ShouldAllBe(i => !i.IsOpen);
        File.Exists(_folder.ApiRuntimeFilePath).ShouldBeFalse();
    }

    private void CheckTheApiIsUp(ApiServer api)
    {
        var signIn = api.SignInUrl.ShouldNotBeNull();
        signIn.Host.ShouldBe("127.0.0.1");

        var runtime = ApiRuntimeFile.TryRead(_folder.ApiRuntimeFilePath).ShouldNotBeNull();
        runtime.Port.ShouldBe(signIn.Port);
        runtime.ProcessId.ShouldBe(Environment.ProcessId);

        // Off the STA thread: nothing pumps its synchronization context while this waits.
        Task.Run(async () =>
        {
            using var client = new HttpClient { BaseAddress = runtime.BaseAddress };
            using (var anonymous = await client.GetAsync(new Uri("/api/status", UriKind.Relative)))
            {
                anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runtime.Token);
            using var status = await client.GetAsync(new Uri("/api/status", UriKind.Relative));
            status.StatusCode.ShouldBe(HttpStatusCode.OK);

#if !DEBUG
            // Release builds always embed the dashboard (#30).
            using var dashboard = await client.GetAsync(new Uri("/", UriKind.Relative));
            dashboard.StatusCode.ShouldBe(HttpStatusCode.OK);
            dashboard.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
#endif
        }).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<ActivityInterval>> ReadIntervalsAsync()
    {
        using var database = TimeglassDatabase.Open(_folder.DatabasePath);
        var repository = new IntervalRepository(database);
        await using (repository.ConfigureAwait(false))
        {
            var now = TimeProvider.System.GetUtcNow();
            return await repository.GetRangeAsync(now.AddHours(-1), now.AddHours(1), TestContext.Current.CancellationToken);
        }
    }
}
